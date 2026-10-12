// Isolated integration test. Requires Docker, Node.js and a Release API build.
// Run: node scripts/test-password-change.mjs [--serve]
import assert from 'node:assert/strict';
import { randomUUID, randomBytes, createHmac } from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import { createServer, request as proxyRequest } from 'node:http';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import { resolve, extname } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const fixture = 'cloneweb-password-test-' + randomUUID().slice(0, 8);
const secret = randomBytes(32).toString('hex');
const apiPort = 5189, webPort = 5190;
let api, web, containerCreated = false;
const docker = (args, options = {}) => execFileSync('docker', args, {encoding:'utf8', windowsHide:true, ...options}).trim();
const delay = ms => new Promise(r => setTimeout(r, ms));
const outputDir = resolve(root, '.local/password-test');
await mkdir(outputDir, {recursive:true});

async function startApi(dbPort) {
  const log = createWriteStream(resolve(outputDir, 'api.log'), {flags:'a'});
  api = spawn('dotnet', [resolve(root, 'backend/TruyenDex.Api/bin/Release/net10.0/TruyenDex.Api.dll')], {
    cwd: resolve(root, 'backend/TruyenDex.Api'), windowsHide:true,
    env: {...process.env, ASPNETCORE_ENVIRONMENT:'Development', ASPNETCORE_URLS:`http://127.0.0.1:${apiPort}`,
      ConnectionStrings__Database:`Host=127.0.0.1;Port=${dbPort};Database=password_test;Username=password_test;Password=${secret}`,
      ConnectionStrings__Redis:'', Meilisearch__Url:'', Jwt__Key:secret,
      Seed__AdminEmail:'admin', Seed__AdminPassword:secret}, stdio:['ignore','pipe','pipe']
  });
  api.stdout.pipe(log); api.stderr.pipe(log);
  for (let i = 0; i < 100; i++) {
    if (api.exitCode !== null) throw new Error('Test API exited. Inspect .local/password-test/api.log');
    try { const r = await fetch(`http://127.0.0.1:${apiPort}/api/auth/google/config`); if (r.ok) return; } catch {}
    await delay(200);
  }
  throw new Error('Test API did not start');
}
async function stopApi() {
  if (api && api.exitCode === null) {
    await new Promise(r => { api.once('exit', r); api.kill(); });
  }
}
async function request(path, {method='GET', body, token, expected=200} = {}) {
  const res = await fetch(`http://127.0.0.1:${apiPort}/api` + path, {
    method, headers:{...(body ? {'Content-Type':'application/json'} : {}), ...(token ? {Authorization:'Bearer ' + token} : {})},
    body:body ? JSON.stringify(body) : undefined
  });
  assert.equal(res.status, expected, `${method} ${path}: expected ${expected}, got ${res.status}`);
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}
const legacyToken = token => {
  const [header, payload] = token.split('.');
  const claims = JSON.parse(Buffer.from(payload, 'base64url'));
  delete claims.pwd_ver;
  const unsigned = header + '.' + Buffer.from(JSON.stringify(claims)).toString('base64url');
  return unsigned + '.' + createHmac('sha256', secret).update(unsigned).digest('base64url');
};
const change = (token, currentPassword, newPassword, confirmPassword = newPassword, expected=200) =>
  request('/auth/change-password', {method:'POST', token, body:{currentPassword,newPassword,confirmPassword}, expected});

try {
  docker(['run','-d','--name',fixture,'--env','POSTGRES_PASSWORD','--env','POSTGRES_DB=password_test','--env','POSTGRES_USER=password_test','-p','127.0.0.1::5432','postgres:17-alpine'], {env:{...process.env,POSTGRES_PASSWORD:secret}});
  containerCreated = true;
  const dbPort = Number(docker(['port',fixture,'5432/tcp']).split(':').at(-1));
  for (let i = 0; i < 50; i++) {
    try { docker(['exec',fixture,'pg_isready','-U','password_test','-d','password_test'], {stdio:'pipe'}); break; } catch { await delay(200); }
  }
  await startApi(dbPort);
  const oldPassword = randomBytes(16).toString('hex'), newPassword = randomBytes(64).toString('hex');
  const reader = await request('/auth/register', {method:'POST', body:{email:'reader@example.test',name:'Người dùng kiểm thử',password:oldPassword}});
  const secondSession = await request('/auth/login', {method:'POST', body:{email:reader.user.email,password:oldPassword}});
  const legacy = legacyToken(reader.token);
  await request('/auth/me', {token:legacy});
  await change(undefined, oldPassword, newPassword, newPassword, 401);
  await change(reader.token, 'wrong-current', newPassword, newPassword, 400);
  await change(reader.token, oldPassword, 'short', 'short', 400);
  await change(reader.token, oldPassword, 'x'.repeat(129), 'x'.repeat(129), 400);
  await change(reader.token, oldPassword, newPassword, 'mismatch', 400);
  await change(reader.token, oldPassword, oldPassword, oldPassword, 400);
  console.log('PASS: authentication, current password, length, confirmation and same-password validation');

  const changed = await change(reader.token, oldPassword, newPassword);
  assert.equal(changed.user.id, reader.user.id);
  assert.equal(changed.user.canChangePassword, true);
  for (const token of [reader.token, secondSession.token, legacy]) await request('/auth/me', {token,expected:401});
  await request('/auth/me', {token:changed.token});
  await request('/auth/login', {method:'POST',body:{email:reader.user.email,password:oldPassword},expected:401});
  await request('/auth/login', {method:'POST',body:{email:reader.user.email,password:newPassword}});
  console.log('PASS: changed password works, old password fails, all old sessions expire, current session stays signed in');

  const other = await request('/auth/register', {method:'POST', body:{email:'google@example.test',name:'Google kiểm thử',password:oldPassword}});
  const otherPassword = randomBytes(16).toString('hex');
  const foreignIdPayload = {currentPassword:oldPassword,newPassword:otherPassword,confirmPassword:otherPassword,userId:reader.user.id};
  const changedOther = await request('/auth/change-password', {method:'POST',token:other.token,body:foreignIdPayload});
  assert.equal(changedOther.user.id, other.user.id);
  await request('/auth/login', {method:'POST',body:{email:reader.user.email,password:newPassword}});
  docker(['exec',fixture,'psql','-U','password_test','-d','password_test','-c',`UPDATE "Users" SET "PasswordHash"='' WHERE "Id"='${other.user.id}'`]);
  const googleUser = await request('/auth/me', {token:changedOther.token});
  assert.equal(googleUser.canChangePassword, false);
  const googleResult = await change(changedOther.token, oldPassword, newPassword, newPassword, 400);
  assert.match(googleResult.message, /Google/);
  await request('/auth/login', {method:'POST',body:{email:other.user.email,password:oldPassword},expected:401});
  console.log('PASS: user isolation and Google-only account handling');

  // Restart resets the test-only limiter and verifies persistence/seed behavior.
  await stopApi(); await startApi(dbPort);
  const admin = await request('/auth/login', {method:'POST',body:{email:'admin',password:secret}});
  const adminPassword = randomBytes(5).toString('hex');
  await change(admin.token, secret, adminPassword);
  await stopApi(); await startApi(dbPort);
  await request('/auth/login', {method:'POST',body:{email:'admin',password:secret},expected:401});
  await request('/auth/login', {method:'POST',body:{email:'admin',password:'admin123'},expected:401});
  await request('/auth/login', {method:'POST',body:{email:'admin',password:adminPassword}});
  const persisted = await request('/auth/login', {method:'POST',body:{email:reader.user.email,password:newPassword}});
  console.log('PASS: reader/admin passwords survive API restart; default admin password cannot bypass verification');

  if (process.argv.includes('--serve')) {
    const mime = {'.html':'text/html','.js':'application/javascript','.css':'text/css','.svg':'image/svg+xml'};
    web = createServer(async (req,res) => {
      if (req.url.startsWith('/api/')) {
        const upstream = proxyRequest(`http://127.0.0.1:${apiPort}` + req.url, {method:req.method,headers:req.headers}, r => {res.writeHead(r.statusCode,r.headers);r.pipe(res);});
        upstream.on('error', () => {res.writeHead(502);res.end();}); req.pipe(upstream); return;
      }
      const path = new URL(req.url, 'http://localhost').pathname;
      try {
        const relative = path === '/' ? 'index.html' : path.slice(1);
        const file = resolve(root,'frontend/dist/frontend/browser',relative);
        assert.ok(file.startsWith(resolve(root,'frontend/dist/frontend/browser')));
        const data = await readFile(file); res.setHeader('Content-Type',mime[extname(file)] || 'application/octet-stream');res.end(data);
      } catch { res.setHeader('Content-Type','text/html');res.end(await readFile(resolve(root,'frontend/dist/frontend/browser/index.html'))); }
    });
    await new Promise(r => web.listen(webPort,'127.0.0.1',r));
    await writeFile(resolve(outputDir,'preview.json'), JSON.stringify({url:`http://127.0.0.1:${webPort}/dang-nhap?token=${encodeURIComponent(persisted.token)}&returnUrl=/doi-mat-khau`}));
    console.log(`Preview ready at http://127.0.0.1:${webPort}. Send a newline to stop and remove the disposable database.`);
    process.stdin.resume(); await new Promise(r => process.stdin.once('data',r));
    await new Promise(r => web.close(r));
  }
} finally {
  await stopApi();
  if (containerCreated) docker(['rm','-f',fixture]);
}
