$ErrorActionPreference='Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$dest=Join-Path $PWD "backups/$stamp"
New-Item -ItemType Directory -Path $dest | Out-Null
try {
 docker compose stop jarvis-api jarvis-browser
 if ($LASTEXITCODE -ne 0) { throw 'Dienste konnten nicht gestoppt werden.' }
 # pg_dump writes inside the DB container; avoid binary corruption from PowerShell redirection.
 docker compose exec -T postgres pg_dump -U jarvis -d jarvis -Fc -f /tmp/jarvis-backup.dump
 if ($LASTEXITCODE -ne 0) { throw 'Datenbank-Backup fehlgeschlagen.' }
 docker compose cp postgres:/tmp/jarvis-backup.dump "$dest/postgres.dump"
 if ($LASTEXITCODE -ne 0) { throw 'Backup-Kopie fehlgeschlagen.' }
 Copy-Item -LiteralPath '.env' -Destination "$dest/config.env"
 docker compose cp jarvis-api:/data/jarvis "$dest/uploads"
 if ($LASTEXITCODE -ne 0) { throw 'Upload-Backup fehlgeschlagen.' }
 docker compose cp jarvis-api:/data/obsidian "$dest/obsidian"
 if ($LASTEXITCODE -ne 0) { throw 'Vault-Backup fehlgeschlagen.' }
 docker compose cp jarvis-browser:/data "$dest/browser"
 if ($LASTEXITCODE -ne 0) { throw 'Browser-Backup fehlgeschlagen.' }
 Copy-Item -LiteralPath 'docker-compose.yml' -Destination $dest
 Write-Host "Backup: $dest — enthält Secrets, verschlüsselt offline aufbewahren."
} finally { docker compose start jarvis-api jarvis-browser }
