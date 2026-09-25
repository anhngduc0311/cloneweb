#!/usr/bin/env bash
# Run on the Ubuntu VPS from a checkout of this repository.
set -Eeuo pipefail
umask 077

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"
ENV_FILE="$ROOT_DIR/.env.production"
COMPOSE_FILE="$ROOT_DIR/compose.production.yaml"
INSTALL_DOCKER=false
CHECK_ONLY=false

log() { printf '\n[deploy] %s\n' "$*"; }
die() { printf '\n[deploy] ERROR: %s\n' "$*" >&2; exit 1; }
usage() {
  cat <<'HELP'
Usage: bash deploy.sh [--install-docker] [--check]

  --install-docker  Install Docker Engine + Compose from Docker's Ubuntu APT
                    repository if Docker/Compose is missing (requires sudo).
  --check           Prepare .env.production and validate Compose; do not deploy.
  --help            Show this help.

First deployment: bash deploy.sh --install-docker
Update:           git pull --ff-only && bash deploy.sh
Custom port:      WEB_PORT=8080 bash deploy.sh

Persistent settings and generated credentials: .env.production (mode 600).
Only the web port is published. HTTPS is configured separately at your proxy.
HELP
}

for arg in "$@"; do
  case "$arg" in
    --install-docker) INSTALL_DOCKER=true ;;
    --check) CHECK_ONLY=true ;;
    --help|-h) usage; exit 0 ;;
    *) die "Unknown argument: $arg. Use --help." ;;
  esac
done

[[ "$(uname -s)" == Linux && -f /etc/os-release ]] || die 'Run this script on the Ubuntu VPS.'
# Standard OS metadata supplied by the operating system, not the application env.
. /etc/os-release
[[ "$ID" == ubuntu ]] || die 'This script supports Ubuntu only.'
[[ -f "$COMPOSE_FILE" ]] || die 'Missing compose.production.yaml; upload the complete project.'

command -v flock >/dev/null || die 'Install util-linux (flock) first.'
exec 9>"$ROOT_DIR/.deploy.lock"
flock -n 9 || die 'Another deployment is running in this checkout.'

as_root() {
  if (( EUID == 0 )); then "$@"; else sudo -- "$@"; fi
}

if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
  "$INSTALL_DOCKER" || die 'Docker Engine + Compose required. Re-run with --install-docker.'
  (( EUID == 0 )) || command -v sudo >/dev/null || die 'sudo is required to install Docker.'
  # Do not remove existing runtimes automatically: they may serve other apps.
  for pkg in docker.io docker-compose docker-compose-v2 docker-doc docker-buildx podman-docker containerd runc; do
    if [[ "$(dpkg-query -W -f='${Status}' "$pkg" 2>/dev/null || true)" == 'install ok installed' ]]; then
      die "Conflicting package $pkg is installed. Follow Docker's Ubuntu migration instructions first."
    fi
  done
  log 'Installing Docker from the official Ubuntu APT repository...'
  as_root apt-get update
  as_root apt-get install -y ca-certificates curl
  as_root install -m 0755 -d /etc/apt/keyrings
  as_root curl -fsSL --retry 3 https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  as_root chmod a+r /etc/apt/keyrings/docker.asc
  as_root tee /etc/apt/sources.list.d/docker.sources >/dev/null <<APT
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: ${UBUNTU_CODENAME:-$VERSION_CODENAME}
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
APT
  as_root chmod 644 /etc/apt/sources.list.d/docker.sources
  as_root apt-get update
  as_root apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  as_root systemctl enable --now docker
fi

DOCKER=(docker)
if ! docker info >/dev/null 2>&1; then
  if (( EUID != 0 )) && command -v sudo >/dev/null && sudo docker info >/dev/null 2>&1; then
    DOCKER=(sudo docker)
  else
    die 'Docker daemon is unavailable. Check: sudo systemctl status docker'
  fi
fi
"${DOCKER[@]}" compose version >/dev/null

random_hex() { od -An -N "$1" -tx1 /dev/urandom | tr -d ' \n'; }
if [[ ! -f "$ENV_FILE" ]]; then
  existing_volumes="$("${DOCKER[@]}" volume ls -q --filter label=com.docker.compose.project=akatruyen)"
  [[ -z "$existing_volumes" ]] || die 'Production volumes already exist. Restore .env.production from backup; do not regenerate database credentials.'
  log 'Generating first-run production credentials...'
  # Create atomically, so an interrupted write cannot leave partial credentials.
  temp_env="$(mktemp "$ROOT_DIR/.env.production.tmp.XXXXXX")"
  trap 'rm -f -- "${temp_env:-}"' EXIT
  cat >"$temp_env" <<ENV
POSTGRES_PASSWORD=$(random_hex 32)
REDIS_PASSWORD=$(random_hex 32)
MEILI_MASTER_KEY=$(random_hex 32)
JWT_KEY=$(random_hex 48)
ADMIN_EMAIL=admin@akatruyen.local
ADMIN_PASSWORD=$(random_hex 24)aA!
WEB_BIND=0.0.0.0
WEB_PORT=80
ENV
  mv -- "$temp_env" "$ENV_FILE"
  trap - EXIT
fi
chmod 600 "$ENV_FILE"

COMPOSE=("${DOCKER[@]}" compose --project-name akatruyen --env-file "$ENV_FILE" -f "$COMPOSE_FILE")
on_error() {
  local code=$?
  printf '\n[deploy] Failed (exit %s). Existing volumes and credentials are preserved.\n' "$code" >&2
  "${COMPOSE[@]}" ps >&2 || true
  printf '[deploy] Inspect logs: docker compose -p akatruyen --env-file .env.production -f compose.production.yaml logs --tail=100\n' >&2
  exit "$code"
}
trap on_error ERR

log 'Validating production configuration...'
"${COMPOSE[@]}" config --quiet
if "$CHECK_ONLY"; then
  log 'Configuration is valid. No containers were changed.'
  exit 0
fi

log 'Pulling infrastructure images and building the application...'
"${COMPOSE[@]}" pull db redis meilisearch
"${COMPOSE[@]}" build --pull api web

log 'Starting services and waiting for container health...'
# Recreate the frontend with the API so nginx resolves its current container IP.
"${COMPOSE[@]}" up -d --wait --wait-timeout 180 --force-recreate api web

log 'Checking API, database, Redis and Meilisearch through the web proxy...'
healthy=false
for ((attempt=1; attempt<=30; attempt++)); do
  if health="$("${COMPOSE[@]}" exec -T web wget -q -T 5 -O - http://127.0.0.1/api/health 2>/dev/null)" &&
     printf '%s' "$health" | grep -Eq '"database"[[:space:]]*:[[:space:]]*"connected"' &&
     printf '%s' "$health" | grep -Eq '"redis"[[:space:]]*:[[:space:]]*"connected"' &&
     printf '%s' "$health" | grep -Eq '"meilisearch"[[:space:]]*:[[:space:]]*"connected"'; then
    healthy=true
    break
  fi
  sleep 2
done
if ! "$healthy"; then
  printf '[deploy] API health check failed.\n' >&2
  false # Trigger the shared error report and return a nonzero status.
fi

"${COMPOSE[@]}" ps
log 'Deployment successful.'
printf 'Web binding: '
"${COMPOSE[@]}" port web 80
printf 'Open http://<VPS-IP>:<published-port> (or your configured HTTPS proxy).\n'
printf 'Admin credentials: %s (not printed). Keep this file backed up with the database.\n' "$ENV_FILE"
