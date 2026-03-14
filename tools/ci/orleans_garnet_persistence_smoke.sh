#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

GARNET_HOST="127.0.0.1"
GARNET_PORT="6379"
GARNET_CONNECTION_STRING="${AEVATAR_TEST_GARNET_CONNECTION_STRING:-${GARNET_HOST}:${GARNET_PORT},abortConnect=false,connectRetry=20,connectTimeout=5000,syncTimeout=5000,asyncTimeout=5000}"
GARNET_CONTAINER_NAME="aevatar-garnet"
GARNET_READY_LOG="Ready to accept connections"
GARNET_WAIT_ATTEMPTS="${GARNET_WAIT_ATTEMPTS:-60}"
GARNET_WAIT_INTERVAL_SECONDS="${GARNET_WAIT_INTERVAL_SECONDS:-2}"

cleanup() {
  docker compose stop garnet >/dev/null 2>&1 || true
  docker compose rm -f garnet >/dev/null 2>&1 || true
}
trap cleanup EXIT

probe_garnet_host_port() {
  if command -v nc >/dev/null 2>&1; then
    nc -z "${GARNET_HOST}" "${GARNET_PORT}" >/dev/null 2>&1
    return $?
  fi

  bash -lc "exec 3<>/dev/tcp/${GARNET_HOST}/${GARNET_PORT}" >/dev/null 2>&1
}

wait_garnet() {
  for ((attempt = 1; attempt <= GARNET_WAIT_ATTEMPTS; attempt++)); do
    state="$(docker inspect --format '{{.State.Status}}' "${GARNET_CONTAINER_NAME}" 2>/dev/null || true)"
    if [[ "${state}" == "running" ]] &&
       docker compose logs --no-color garnet 2>/dev/null | rg -q "${GARNET_READY_LOG}" &&
       probe_garnet_host_port; then
      echo "Garnet reported ready on ${GARNET_HOST}:${GARNET_PORT}."
      return 0
    fi

    echo "Waiting for Garnet on ${GARNET_HOST}:${GARNET_PORT}... (attempt ${attempt}/${GARNET_WAIT_ATTEMPTS}, state=${state:-unknown})"
    sleep "${GARNET_WAIT_INTERVAL_SECONDS}"
  done

  echo "Garnet failed to become reachable."
  docker compose ps garnet || true
  docker port "${GARNET_CONTAINER_NAME}" || true
  docker compose logs --tail=120 garnet || true
  return 1
}

echo "Starting Garnet..."
docker compose up -d garnet
wait_garnet

echo "Running Orleans + Garnet persistence integration test..."
AEVATAR_TEST_GARNET_CONNECTION_STRING="${GARNET_CONNECTION_STRING}" \
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj \
  --nologo \
  --tl:off \
  -m:1 \
  -p:UseSharedCompilation=false \
  -p:NuGetAudit=false \
  --filter "FullyQualifiedName~OrleansGarnetPersistenceIntegrationTests"
