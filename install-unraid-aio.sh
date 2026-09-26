#!/usr/bin/env bash
# Explicit single-container migration. Never remove volumes or regenerate existing keys.
set -Eeuo pipefail
umask 077
source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$source_dir/scripts/unraid-common.sh"
root=/mnt/user/appdata/jarvis
env_file="$source_dir/.env"
migrate=false
[[ "${1:-}" != --migrate ]] || migrate=true
[[ -z "${1:-}" || "${1:-}" == --migrate ]] || { jarvis_fail 'Aufruf: install-unraid-aio.sh [--migrate]'; exit 1; }
[[ -f /etc/unraid-version && "$EUID" == 0 ]] || { jarvis_fail 'Im Unraid-Terminal als root starten.'; exit 1; }
[[ -d /mnt/user/appdata ]] || { jarvis_fail 'appdata fehlt.'; exit 1; }
for command in docker openssl flock tar stat du df awk curl sha256sum ip ss; do command -v "$command" >/dev/null || { jarvis_fail "$command fehlt"; exit 1; }; done
mkdir -p "$root"
exec 9>"$root/.install.lock"
flock -n 9 || { jarvis_fail 'Ein Installer läuft bereits.'; exit 1; }
[[ ! -L "$env_file" ]] || { jarvis_fail '.env ist ein Link.'; exit 1; }
docker --host unix:///var/run/docker.sock info >/dev/null
jarvis_install_compose "$root"
old_ids=()
mapfile -t old_ids < <(docker ps -aq --filter label=com.docker.compose.project=jarvis)
if ((${#old_ids[@]})) && ! "$migrate"; then
  jarvis_fail 'Bisherigen Stack gefunden. Für Sicherung und Umstellung --migrate verwenden.'; exit 1
fi
if ((${#old_ids[@]})); then
  # Refuse migration from named volumes or different host paths.
  for pair in postgres:postgres redis:redis jarvis-api:api jarvis-browser:browser; do
    service="${pair%%:*}"; directory="${pair#*:}"
    id="$(docker ps -aq --filter label=com.docker.compose.project=jarvis --filter "label=com.docker.compose.service=$service")"
    [[ -n "$id" && "$id" != *$'\n'* ]] || { jarvis_fail "Genau ein alter $service-Container erforderlich."; exit 1; }
    mounts="$(docker inspect --format '{{range .Mounts}}{{println .Type .Source}}{{end}}' "$id")"
    grep -Fxq "bind $root/$directory" <<< "$mounts" || { jarvis_fail "Abweichender Speicher für $service. Keine automatische Migration."; exit 1; }
  done
fi
existing=false
for directory in postgres redis api browser obsidian; do
  [[ ! -L "$root/$directory" ]] || { jarvis_fail "Symlink abgewiesen: $directory"; exit 1; }
  if [[ -d "$root/$directory" && -n "$(find "$root/$directory" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then existing=true; fi
done
if "$existing" && [[ ! -f "$env_file" ]]; then jarvis_fail 'Daten vorhanden: originale .env fehlt. Abbruch ohne Schlüsselerzeugung.'; exit 1; fi
[[ -f "$env_file" ]] || cp "$source_dir/.env.example" "$env_file"
for key in POSTGRES_PASSWORD REDIS_PASSWORD MASTER_KEY BROWSER_MASTER_KEY BROWSER_TOKEN SETUP_TOKEN SIP_SERVICE_TOKEN LOCAL_NETWORK_TOKEN; do
  value="$(jarvis_env_get "$env_file" "$key")"
  if [[ -z "$value" ]]; then
    if "$existing"; then jarvis_fail "Bestehende Daten, aber $key fehlt. Original wiederherstellen."; exit 1; fi
    case "$key" in MASTER_KEY|BROWSER_MASTER_KEY) value="$(openssl rand -base64 32)";; *) value="$(openssl rand -hex 32)";; esac
    jarvis_env_set "$env_file" "$key" "$value"
  fi
done
# Snapshot configuration before changing URL or bind address.
config_backup="$(mktemp "$root/aio-before.XXXXXX.env")"
cp -p "$env_file" "$config_backup"
address="$(jarvis_env_get "$env_file" AIO_BIND_ADDRESS)"
[[ -n "$address" ]] || address="$(jarvis_detect_address)"
jarvis_private_ipv4 "$address" || { jarvis_fail 'AIO_BIND_ADDRESS muss die private Unraid-IP sein.'; exit 1; }
port="$(jarvis_env_get "$env_file" AIO_HTTP_PORT)"
[[ -n "$port" ]] || port="$(jarvis_choose_port 8090 "$(ss -H -ltn)" "$(docker ps --format '{{.Ports}}')")"
[[ "$port" =~ ^[0-9]+$ ]] && ((port>=1024 && port<=65535)) || { jarvis_fail 'Ungültiger AIO-Port.'; exit 1; }
jarvis_env_set "$env_file" PUBLIC_URL https://jarvis.mc-media.eu
jarvis_env_set "$env_file" AIO_BIND_ADDRESS "$address"
jarvis_env_set "$env_file" AIO_HTTP_PORT "$port"
jarvis_env_set "$env_file" AIO_DATA_ROOT "$root"
# Do not silently drop active optional profiles.
profiles="$(jarvis_env_get "$env_file" COMPOSE_PROFILES)"
if [[ ",$profiles," == *,search,* || ",$profiles," == *,local-ai,* || ",$profiles," == *,antivirus,* ]]; then
  jarvis_fail 'Aktive SearXNG/Ollama/ClamAV-Profile benötigen vorher eine separate Migrationsentscheidung. Alte Container bleiben unverändert.'; exit 1
fi
if [[ ",$profiles," == *,sip,* ]]; then jarvis_env_set "$env_file" AIO_SIP_ENABLED true; fi
for directory in postgres redis api browser obsidian; do mkdir -p "$root/$directory"; done
bash "$source_dir/aio-compose.sh" config --quiet
# Fail on invalid keys BEFORE stopping the old installation.
bash "$source_dir/aio-compose.sh" build
bash "$source_dir/aio-compose.sh" run --rm --no-deps --entrypoint python3 jarvis -c 'import sys,os; sys.path.insert(0,"/app/aio"); from manager import validate; validate(os.environ)'
old_running=()
mapfile -t old_running < <(docker ps -q --filter label=com.docker.compose.project=jarvis)
if ((${#old_running[@]})); then docker stop -t 120 "${old_running[@]}"; fi
if "$existing" && ((${#old_ids[@]})); then
  backup="$(mktemp -d "$root/backups-aio.XXXXXX")"
  cp -p "$config_backup" "$backup/original.env"
  required="$(du -sk "$root/postgres" "$root/redis" "$root/api" "$root/browser" "$root/obsidian" | awk '{sum+=$1} END{print sum}')"
  available="$(df -Pk "$root" | awk 'END{print $4}')"
  if (( available < required + 1048576 )) || ! tar -cpf "$backup/data.tar" -C "$root" postgres redis api browser obsidian; then
    cp -p "$config_backup" "$env_file"
    if ((${#old_running[@]})); then docker start "${old_running[@]}"; fi
    jarvis_fail "Sicherung fehlgeschlagen/zu wenig Platz. Alter Stack wieder gestartet. Prüfe $backup"; exit 1
  fi
  printf 'Konsistente Datensicherung und Originalschlüssel: %s\n' "$backup"
fi
if ! bash "$source_dir/aio-compose.sh" up -d --wait --wait-timeout 600; then
  printf 'AIO-Start fehlgeschlagen. Alte Container bleiben gestoppt, Daten und Sicherung bleiben erhalten.\n'
  printf 'Logs: bash %q/aio-compose.sh logs --tail=100\n' "$source_dir"
  exit 1
fi
# Remove ONLY identified, stopped old containers; never remove volumes or data directories.
if ((${#old_ids[@]})); then docker rm "${old_ids[@]}"; fi
{
  printf 'JARVIS: https://jarvis.mc-media.eu\nZoraxy-Ziel: http://%s:%s\n' "$address" "$port"
  printf 'Setup-Token: %s\n' "$(jarvis_env_get "$env_file" SETUP_TOKEN)"
} > "$root/START-HIER.txt"
chmod 600 "$root/START-HIER.txt"
printf '\nEin JARVIS-Container läuft. Zoraxy: jarvis.mc-media.eu -> http://%s:%s (Upstream TLS AUS).\n' "$address" "$port"
printf 'Setup-Token bleibt lokal in %s/START-HIER.txt.\nAlte Container entfernt; Daten, Caddy-Verzeichnisse und Sicherungen bleiben erhalten.\n' "$root"
