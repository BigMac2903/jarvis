#!/usr/bin/env bash
# This file can also be copied as a standalone installer to the Unraid server.
jarvis_repo_url() {
  local url="${1%/}" owner repo
  [[ "$url" =~ ^https://github\.com/([A-Za-z0-9][A-Za-z0-9-]*)/([A-Za-z0-9_.-]+)$ ]] || return 1
  owner="${BASH_REMATCH[1]}"; repo="${BASH_REMATCH[2]%.git}"
  [[ -n "$repo" && "$repo" != . && "$repo" != .. ]] || return 1
  printf 'https://github.com/%s/%s.git\n' "$owner" "$repo"
}

jarvis_github_main() {
local repo url root target checkout command
[[ -f /etc/unraid-version && "$EUID" -eq 0 ]] || { printf 'Bitte im Unraid-Terminal als root starten.\n' >&2; exit 1; }
repo="${1:-}"
if [[ -z "$repo" ]]; then read -r -p 'Dein GitHub-Repository-Link: ' repo; fi
if ! url="$(jarvis_repo_url "$repo")"; then
  printf 'Bitte https://github.com/DEIN-NAME/DEIN-REPOSITORY ohne Token oder /tree/... angeben.\n' >&2; exit 1
fi
for command in git docker flock; do command -v "$command" >/dev/null || { printf '%s fehlt.\n' "$command" >&2; exit 1; }; done
[[ -d /mnt/user/appdata ]] || { printf 'Array/Pool und appdata-Freigabe zuerst bereitstellen.\n' >&2; exit 1; }
root=/mnt/user/appdata/jarvis
mkdir -p "$root"
[[ ! -f "$root/docker-compose.yml" && ! -f "$root/.env" ]] || {
  printf 'Vorhandene manuelle Installation gefunden. Dort install-unraid.sh verwenden.\n' >&2; exit 1;
}
exec 8>"$root/.checkout.lock"
flock -n 8 || { printf 'Ein Checkout läuft bereits.\n' >&2; exit 1; }
target="$root/source"
if [[ -e "$target" || -L "$target" ]]; then
  printf 'Quellverzeichnis existiert bereits: %s\nKein Überschreiben. Dort install-unraid.sh erneut ausführen.\n' "$target" >&2; exit 1
fi
checkout="$(mktemp -d "$root/.checkout.XXXXXX")"
git -c http.sslVerify=true clone --depth 1 --single-branch -- "$url" "$checkout"
[[ -f "$checkout/install-unraid.sh" && -f "$checkout/docker-compose.unraid.yml" && -f "$checkout/src/Jarvis.Api/Jarvis.Api.csproj" ]] || {
  printf 'Im Repository fehlen JARVIS-Dateien auf oberster Ebene. Download bleibt unter %s erhalten.\n' "$checkout" >&2; exit 1;
}
mv -T -- "$checkout" "$target"
bash "$target/install-unraid.sh"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  set -euo pipefail
  umask 077
  jarvis_github_main "$@"
fi
