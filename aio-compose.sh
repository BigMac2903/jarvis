#!/usr/bin/env bash
set -euo pipefail
source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source "$source_dir/scripts/unraid-common.sh"
args=(--project-name jarvis-aio --project-directory "$source_dir" --env-file "$source_dir/.env" -f "$source_dir/docker-compose.aio.yml")
if [[ "$(jarvis_env_get "$source_dir/.env" AIO_SIP_ENABLED)" == true ]]; then args+=(-f "$source_dir/docker-compose.aio-sip.yml"); fi
jarvis_compose /mnt/user/appdata/jarvis "${args[@]}" "$@"
