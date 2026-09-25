$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
docker info --format '{{.ServerVersion}}'
if ($LASTEXITCODE -ne 0) { throw 'Docker nicht erreichbar.' }
docker compose config --quiet
if ($LASTEXITCODE -ne 0) { throw 'Compose-Konfiguration fehlerhaft.' }
$lines=docker compose ps --format json
if ($LASTEXITCODE -ne 0) { throw 'Containerstatus nicht verfügbar.' }
$containers = ($lines -join "`n") | ConvertFrom-Json
if (-not $containers) { throw 'Keine Container gestartet.' }
$expected=docker compose config --services
if ($LASTEXITCODE -ne 0) { throw 'Dienstliste nicht verfügbar.' }
foreach ($service in $expected) { if ($service -notin $containers.Service) { throw "Container fehlt: $service" } }
foreach ($item in $containers) { if ($item.State -ne 'running' -or $item.Health -ne 'healthy') { throw "Container nicht gesund: $($item.Service) / $($item.State) / $($item.Health)" } }
docker compose exec -T postgres pg_isready -U jarvis -d jarvis
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL-Prüfung fehlgeschlagen.' }
docker compose exec -T jarvis-api curl -fsS http://127.0.0.1:8080/health/ready
if ($LASTEXITCODE -ne 0) { throw 'Backend, Redis oder Migration fehlgeschlagen.' }
docker compose exec -T jarvis-web wget -q -O - http://127.0.0.1:8080/healthz
if ($LASTEXITCODE -ne 0) { throw 'Frontend-Prüfung fehlgeschlagen.' }
Write-Host 'Installation und Healthchecks erfolgreich.'
