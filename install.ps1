param([switch]$ConfigureOnly)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& (Join-Path $PSScriptRoot 'scripts/configure.ps1')
New-Item -ItemType Directory -Force -Path 'data/obsidian','backups' | Out-Null
if ($ConfigureOnly) { Write-Host 'Nur Konfiguration erstellt. Keine Dienste gestartet.'; exit 0 }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw 'Docker Desktop mit Linux-Containern installieren und starten: https://docs.docker.com/desktop/setup/install/windows-install/' }
docker info --format '{{.ServerVersion}}'
if ($LASTEXITCODE -ne 0) { throw 'Docker läuft nicht.' }
docker compose config --quiet
if ($LASTEXITCODE -ne 0) { throw 'Compose-Konfiguration ungültig.' }
docker compose up -d --build --wait --wait-timeout 300
if ($LASTEXITCODE -ne 0) { throw 'Start fehlgeschlagen. docker compose logs prüfen.' }
& (Join-Path $PSScriptRoot 'scripts/verify-installation.ps1')
Write-Host 'Browser: PUBLIC_URL aus .env öffnen. Setup-Token steht als SETUP_TOKEN in .env.'
