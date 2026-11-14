#!/usr/bin/env bash
# End-to-end sync test: builds async-cli (and its Docker image), starts a throw-away server stack with Docker
# Compose (project async-e2e, port ASYNC_E2E_PORT, default 5190), runs scripts/e2e-sync.cs against it with two local
# agents plus one in a container, then removes the stack. From the repository root:
#   scripts/e2e-sync.sh
#   KEEP=1 scripts/e2e-sync.sh              # leave the stack and artifacts/e2e for a look afterwards
#   NO_DOCKER_AGENT=1 scripts/e2e-sync.sh   # skip the agent in a container
set -euo pipefail
cd "$(dirname "$0")/.."

port="${ASYNC_E2E_PORT:-5190}"
project=async-e2e
work="$PWD/artifacts/e2e"
export ASYNC_PORT="$port"
export ASYNC_MAINTENANCE_POLL_SECONDS=5   # the server looks for folders due for prune/check every 5 s, not 15 min

compose() { docker compose -p "$project" "$@"; }
cleanup() {
  local status=$?
  if [[ "${KEEP:-}" == 1 ]]; then
    echo "Kept the stack (remove it with: docker compose -p $project down -v) and $work."
  else
    compose down -v --remove-orphans >/dev/null 2>&1 || true
    rm -rf "$work"
  fi
  exit $status
}
trap cleanup EXIT

rm -rf "$work"
mkdir -p "$work"
echo "Building async-cli…"
dotnet publish src/ASync.Cli/ASync.Cli.csproj -c Release -o "$work/cli" --nologo -v quiet
docker_args=()
if [[ "${NO_DOCKER_AGENT:-}" != 1 ]]; then
  echo "Building the async-cli image…"
  docker build -q -f Dockerfile.cli -t async-cli:e2e . >/dev/null
  docker_args=(--docker-image async-cli:e2e --docker-network "${project}_default")
fi

echo "Starting the server stack on port $port…"
compose down -v --remove-orphans >/dev/null 2>&1 || true
compose up -d --build --wait async-server postgres >"$work/compose-up.log" 2>&1 || { cat "$work/compose-up.log"; exit 1; }
for _ in $(seq 1 60); do
  curl -fsS "http://localhost:$port/api/auth/setup" >/dev/null 2>&1 && break
  sleep 2
done

dotnet run scripts/e2e-sync.cs -- --cli "$work/cli/async-cli.dll" --server "http://localhost:$port" \
  --work "$work/run" --server-container "$(compose ps -q async-server)" "${docker_args[@]}"
