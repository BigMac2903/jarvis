#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
umask 077
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
dest="backups/$stamp"
mkdir -p "$dest"
# Stop writes for a coherent database/files snapshot; restart even if backup fails.
sip_running=false
case "$(docker compose ps --services --status running)" in *jarvis-sip*) sip_running=true;; esac
restore_services() {
  docker compose start jarvis-api jarvis-browser >/dev/null
  if [ "$sip_running" = true ]; then docker compose start jarvis-sip >/dev/null; fi
}
trap restore_services EXIT
if [ "$sip_running" = true ]; then docker compose stop jarvis-sip; fi
docker compose stop jarvis-api jarvis-browser
docker compose exec -T postgres pg_dump -U jarvis -d jarvis -Fc > "$dest/postgres.dump"
cp .env "$dest/config.env"
docker compose cp jarvis-api:/data/jarvis "$dest/uploads"
docker compose cp jarvis-api:/data/obsidian "$dest/obsidian"
docker compose cp jarvis-browser:/data "$dest/browser"
cp docker-compose.yml "$dest/"
cp docker-compose.sip.yml "$dest/"
echo "Backup: $dest — enthält Master-Key und private Daten. Verschlüsselt offline aufbewahren."
