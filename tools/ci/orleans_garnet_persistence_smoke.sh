#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"
source "${SCRIPT_DIR}/distributed_smoke_common.sh"

GARNET_HOST="127.0.0.1"
GARNET_PORT="6379"
LOCK_OWNER="orleans_garnet_persistence_smoke"

cleanup() {
  docker compose stop garnet >/dev/null 2>&1 || true
  docker compose rm -f garnet >/dev/null 2>&1 || true
  release_distributed_smoke_lock
}
trap cleanup EXIT INT TERM

wait_garnet() {
  for _ in {1..30}; do
    if (echo >"/dev/tcp/${GARNET_HOST}/${GARNET_PORT}") >/dev/null 2>&1; then
      echo "Garnet is reachable on ${GARNET_HOST}:${GARNET_PORT}."
      return 0
    fi

    echo "Waiting for Garnet on ${GARNET_HOST}:${GARNET_PORT}..."
    sleep 1
  done

  echo "Garnet failed to become reachable."
  return 1
}

echo "Starting Garnet..."
acquire_distributed_smoke_lock "${LOCK_OWNER}"
ensure_local_tcp_ports_free "${LOCK_OWNER}" "${GARNET_PORT}"
docker compose up -d garnet
wait_garnet

echo "Running Orleans + Garnet persistence integration test..."
AEVATAR_TEST_GARNET_CONNECTION_STRING="${GARNET_HOST}:${GARNET_PORT}" \
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~OrleansGarnetPersistenceIntegrationTests"
