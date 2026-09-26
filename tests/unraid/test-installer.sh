#!/usr/bin/env bash
# Deterministic Linux checks: no Docker start, network requests or host installation.
set -euo pipefail
repository="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$repository/scripts/unraid-common.sh"
source "$repository/install-from-github.sh"
for address in 192.168.178.20 10.1.2.3 172.16.0.1 172.31.255.254; do jarvis_private_ipv4 "$address"; done
for address in 8.8.8.8 127.0.0.1 172.32.0.1 169.254.1.1 192.168.999.1 010.0.0.1 '192.168.1.1;id' ''; do
  if jarvis_private_ipv4 "$address"; then printf 'Accepted invalid IP: %s\n' "$address"; exit 1; fi
done
[[ "$(jarvis_repo_url https://github.com/owner/repo/)" == https://github.com/owner/repo.git ]]
[[ "$(jarvis_repo_url https://github.com/owner/repo.git)" == https://github.com/owner/repo.git ]]
for url in 'http://github.com/u/r' 'https://github.com.evil/u/r' 'https://token@github.com/u/r' 'https://github.com/u/r/tree/main' 'https://github.com/u/..' 'https://github.com/u/.git' 'https://github.com/u/r?token=x'; do
  if jarvis_repo_url "$url"; then printf 'Accepted invalid repository URL\n'; exit 1; fi
done
[[ "$(jarvis_choose_port 8080 'LISTEN 0 128 0.0.0.0:8080 0.0.0.0:*' '0.0.0.0:8081->80/tcp')" == 8082 ]]
[[ "$(jarvis_choose_port 8443 'LISTEN 0 128 [::]:8443 [::]:*' '')" == 8444 ]]
[[ "$(jarvis_choose_port 8080 '' '0.0.0.0:8080-8083->80-83/tcp, [::]:8080-8083->80-83/tcp')" == 8084 ]]
(
  ip() { printf '%s\n' '1: docker0 inet 172.17.0.1/16 scope global docker0' '2: eth0 inet 10.2.3.4/24 scope global eth0' '3: br0 inet 192.168.178.20/24 scope global br0'; }
  [[ "$(jarvis_detect_address)" == 192.168.178.20 ]]
)
scratch="$(mktemp -d)"
printf 'MASTER_KEY=keep-this-key\nPUBLIC_URL=https://localhost\nOTHER=untouched\n' > "$scratch/.env"
jarvis_env_set "$scratch/.env" PUBLIC_URL 'https://192.168.1.2:8443'
jarvis_env_set "$scratch/.env" HTTP_PORT 8080
[[ "$(jarvis_env_get "$scratch/.env" MASTER_KEY)" == keep-this-key ]]
[[ "$(jarvis_env_get "$scratch/.env" PUBLIC_URL)" == https://192.168.1.2:8443 ]]
[[ "$(jarvis_env_get "$scratch/.env" HTTP_PORT)" == 8080 ]]
[[ "$(stat -c %a "$scratch/.env")" == 600 ]]
jarvis_env_set "$scratch/.env" PAYLOAD '\$(touch should-never-exist)'
[[ "$(jarvis_env_get "$scratch/.env" PAYLOAD)" == '\$(touch should-never-exist)' ]]
ln -s "$scratch/.env" "$scratch/symlink"
if jarvis_env_set "$scratch/symlink" HTTP_PORT 1; then printf 'Symlink env was accepted\n'; exit 1; fi
[[ "$(jarvis_env_get "$scratch/.env" HTTP_PORT)" == 8080 ]]
(
  docker() { return 1; }
  uname() { printf 'x86_64\n'; }
  curl() { touch "$scratch/unexpected-download"; return 1; }
  mkdir -p "$scratch/tools/docker-cli/cli-plugins"
  printf 'damaged binary' > "$scratch/tools/docker-cli/cli-plugins/docker-compose"
  if jarvis_install_compose "$scratch"; then printf 'Invalid Compose checksum accepted\n'; exit 1; fi
  [[ ! -e "$scratch/unexpected-download" ]]
)
printf 'PASS: private IPs, GitHub URLs, address selection, occupied ports, literal configuration, key preservation, symlinks and invalid Compose checksum.\n'
