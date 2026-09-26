#!/usr/bin/env bash
set -euo pipefail
source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$source_dir/scripts/unraid-common.sh"
args=(--project-name jarvis --project-directory "$source_dir" --env-file "$source_dir/.env"
  -f "$source_dir/docker-compose.yml" -f "$source_dir/docker-compose.unraid.yml")
profiles="$(jarvis_env_get "$source_dir/.env" COMPOSE_PROFILES)"
if [[ ",$profiles," == *,sip,* ]]; then args+=(-f "$source_dir/docker-compose.sip.yml"); fi
jarvis_compose /mnt/user/appdata/jarvis "${args[@]}" "$@"
