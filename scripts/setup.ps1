$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (!(Test-Path '.env')) {
    $dbPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $jwtKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $adminPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(12)) + 'aA!'
    @("POSTGRES_PASSWORD=$dbPassword", "JWT_KEY=$jwtKey", 'ADMIN_EMAIL=admin@truyendex.local', "ADMIN_PASSWORD=$adminPassword") | Set-Content '.env'
}
$settings = @{}
Get-Content '.env' | ForEach-Object { if ($_ -match '^([^#=]+)=(.*)$') { $settings[$matches[1]] = $matches[2] } }
@{
    ConnectionStrings = @{ Database = "Host=localhost;Port=54329;Database=truyendex;Username=truyendex;Password=$($settings.POSTGRES_PASSWORD)" }
    Jwt = @{ Key = $settings.JWT_KEY }
    Seed = @{ AdminEmail = $settings.ADMIN_EMAIL; AdminPassword = $settings.ADMIN_PASSWORD }
} | ConvertTo-Json | Set-Content 'backend/TruyenDex.Api/appsettings.Local.json'
Write-Host 'Local configuration ready. Admin credentials are in .env (gitignored).'
