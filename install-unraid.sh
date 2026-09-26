#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$source_dir/scripts/unraid-common.sh"
address=""; configure_only=false
while (($#)); do
  case "$1" in
    --address) [[ $# -ge 2 ]] || exit 2; address="$2"; shift 2 ;;
    --configure-only) configure_only=true; shift ;;
    --help) printf 'bash install-unraid.sh [--address PRIVATE-IP] [--configure-only]\n'; exit 0 ;;
    *) jarvis_fail "Unbekanntes Argument: $1"; exit 2 ;;
  esac
done
[[ -f /etc/unraid-version && "$EUID" -eq 0 ]] || { jarvis_fail 'Bitte im Unraid-Terminal als root starten.'; exit 1; }
[[ -z "$address" ]] || jarvis_private_ipv4 "$address" || { jarvis_fail 'Ungültige private IPv4.'; exit 1; }
for command in docker openssl curl sha256sum ip ss awk grep sort cut flock; do
  command -v "$command" >/dev/null || { jarvis_fail "Benötigter Befehl fehlt: $command"; exit 1; }
done
[[ -d /mnt/user/appdata ]] || { jarvis_fail 'appdata-Freigabe fehlt. Array/Pool starten und appdata anlegen.'; exit 1; }
docker --host unix:///var/run/docker.sock info >/dev/null || { jarvis_fail 'Docker unter Settings → Docker aktivieren.'; exit 1; }
root=/mnt/user/appdata/jarvis
mkdir -p "$root"
exec 9>"$root/.install.lock"
flock -n 9 || { jarvis_fail 'Eine andere JARVIS-Installation läuft bereits.'; exit 1; }
trap 'printf "JARVIS-Installation abgebrochen (Zeile %s). Vorhandene Daten und Schlüssel wurden nicht gelöscht.\n" "$LINENO" >&2' ERR
env_file="$source_dir/.env"
[[ ! -L "$env_file" ]] || { jarvis_fail '.env darf kein symbolischer Link sein.'; exit 1; }

# Never attach an existing stack/database to freshly generated encryption keys.
if [[ ! -f "$env_file" ]]; then
  if [[ -e "$root/postgres/PG_VERSION" || -f "$root/.env" ]] || \
     [[ -n "$(docker --host unix:///var/run/docker.sock ps -aq --filter label=com.docker.compose.project=jarvis)" ]]; then
    jarvis_fail 'Bestehende JARVIS-Installation gefunden. Ihre ursprüngliche .env zuerst in dieses Quellverzeichnis übernehmen; keine neuen Schlüssel erzeugen.'; exit 1
  fi
fi
if [[ ! -e "$root/postgres/PG_VERSION" ]] && docker --host unix:///var/run/docker.sock volume inspect jarvis_postgres-data >/dev/null 2>&1; then
  jarvis_fail 'Vorhandenes PostgreSQL-Volume gefunden. Die Umstellung auf Unraid-Bindmounts benötigt zuerst Backup/Restore.'; exit 1
fi
if [[ -f "$root/postgres/PG_VERSION" ]]; then
  for key in MASTER_KEY BROWSER_MASTER_KEY POSTGRES_PASSWORD REDIS_PASSWORD; do
    [[ -n "$(jarvis_env_get "$env_file" "$key")" ]] || {
      jarvis_fail "Bestehende Daten, aber $key fehlt. Originalschlüssel wiederherstellen."; exit 1;
    }
  done
fi
bash "$source_dir/install.sh" --configure-only
url="$(jarvis_env_get "$env_file" PUBLIC_URL)"
if [[ -n "$address" || "$url" == https://localhost || -z "$url" ]]; then
  [[ -n "$address" ]] || address="$(jarvis_detect_address)"
  if [[ "$url" == https://localhost || -z "$url" ]]; then
    sockets="$(ss -H -ltn)"
    published="$(docker --host unix:///var/run/docker.sock ps --format '{{.Ports}}')"
    http_port="$(jarvis_choose_port 8080 "$sockets" "$published")"
    https_port="$(jarvis_choose_port 8443 "$sockets" "$published")"
    jarvis_env_set "$env_file" HTTP_PORT "$http_port"
    jarvis_env_set "$env_file" HTTPS_PORT "$https_port"
  fi
  https_port="$(jarvis_env_get "$env_file" HTTPS_PORT)"
  [[ "$https_port" =~ ^[0-9]+$ ]] && ((https_port>0 && https_port<=65535)) || { jarvis_fail 'HTTPS_PORT ungültig.'; exit 1; }
  jarvis_env_set "$env_file" PUBLIC_URL "https://$address:$https_port"
  jarvis_env_set "$env_file" SITE_ADDRESS "$address"
fi
jarvis_env_set "$env_file" OBSIDIAN_HOST_PATH "$root/obsidian"
for directory in postgres redis api browser caddy caddy-config obsidian; do
  [[ ! -L "$root/$directory" ]] || { jarvis_fail "Datenverzeichnis ist ein Link: $directory"; exit 1; }
  mkdir -p "$root/$directory"
done
# Only the mount roots: never recursively change ownership of an existing vault.
chown 1654:1654 "$root/api" "$root/obsidian"
chown 10001:10001 "$root/browser"
chmod 700 "$root/api" "$root/browser" "$root/obsidian" "$root/caddy" "$root/caddy-config"
jarvis_install_compose "$root"
bash "$source_dir/unraid-compose.sh" config --quiet
if "$configure_only"; then printf 'Konfiguration bereit. Start: bash %q/unraid-compose.sh up -d --build --wait --wait-timeout 600\n' "$source_dir"; exit 0; fi
printf 'Baue JARVIS und starte die Container. Der erste Build kann länger dauern.\n'
bash "$source_dir/unraid-compose.sh" up -d --build --wait --wait-timeout 600
bash "$source_dir/unraid-compose.sh" cp reverse-proxy:/data/caddy/pki/authorities/local/root.crt "$root/caddy-root.crt" || \
  printf 'Root-Zertifikat noch nicht verfügbar. Nach erstem HTTPS-Aufruf mit unraid-compose.sh cp exportieren.\n'
url="$(jarvis_env_get "$env_file" PUBLIC_URL)"
{
  printf 'JARVIS: %s\n' "$url"
  printf 'Setup-Token: %s\n' "$(jarvis_env_get "$env_file" SETUP_TOKEN)"
  printf 'Anleitung: %s/docs/GITHUB_UNRAID.md\n' "$source_dir"
} > "$root/START-HIER.txt"
chmod 600 "$root/START-HIER.txt"
printf '\nJARVIS läuft unter: %s\nSetup-Token: %s/START-HIER.txt (nur lokal lesen, nicht veröffentlichen).\n' "$url" "$root"
printf 'Eigenes Root-Zertifikat vor der Anmeldung auf dem Client vertrauen: %s/caddy-root.crt\n' "$root"
