#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
docker info >/dev/null
docker compose config --quiet
for service in $(docker compose config --services); do
  [ -n "$(docker compose ps -q "$service")" ] || { echo "Container fehlt: $service"; exit 1; }
done
ids="$(docker compose ps -q)"
[ -n "$ids" ] || { echo "Keine Container."; exit 1; }
for id in $ids; do
  state="$(docker inspect --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' "$id")"
  [ "$state" = "running healthy" ] || { echo "Container $id: $state"; exit 1; }
done
docker compose exec -T postgres pg_isready -U jarvis -d jarvis
docker compose exec -T jarvis-api curl -fsS http://127.0.0.1:8080/health/ready
docker compose exec -T jarvis-web wget -q -O - http://127.0.0.1:8080/healthz
echo "Installation, PostgreSQL, Redis, Migration und Healthchecks erfolgreich."
