#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
command -v openssl >/dev/null || { echo "openssl fehlt."; exit 1; }
if [ ! -f .env ]; then cp .env.example .env; fi
chmod 600 .env
for key in POSTGRES_PASSWORD REDIS_PASSWORD MASTER_KEY BROWSER_MASTER_KEY BROWSER_TOKEN SETUP_TOKEN SEARXNG_SECRET SIP_SERVICE_TOKEN; do
  if ! grep -q "^$key=" .env; then printf '\n%s=\n' "$key" >> .env; fi
  if grep -q "^$key=$" .env; then
    case "$key" in MASTER_KEY|BROWSER_MASTER_KEY) value="$(openssl rand -base64 32)";; *) value="$(openssl rand -hex 32)";; esac
    KEY="$key" VALUE="$value" awk 'BEGIN {k=ENVIRON["KEY"];v=ENVIRON["VALUE"]} $0==k"=" {$0=k"="v} {print}' .env > .env.tmp
    mv .env.tmp .env
    chmod 600 .env
  fi
done
mkdir -p data/obsidian backups
if [ "${1:-}" = "--configure-only" ]; then echo "Konfiguration erstellt."; exit 0; fi
command -v docker >/dev/null || { echo "Docker Engine und Compose installieren: https://docs.docker.com/engine/install/"; exit 1; }
docker info >/dev/null
docker compose config --quiet
docker compose up -d --build --wait --wait-timeout 300
bash scripts/verify-installation.sh
echo "PUBLIC_URL im Browser öffnen. SETUP_TOKEN steht in .env."
