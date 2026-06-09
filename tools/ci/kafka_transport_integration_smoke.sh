#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"
source "${SCRIPT_DIR}/distributed_smoke_common.sh"

KAFKA_CONTAINER="aevatar-kafka"
KAFKA_PORT="9092"
LOCK_OWNER="kafka_transport_integration_smoke"

cleanup() {
  docker compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  release_distributed_smoke_lock
}
trap cleanup EXIT INT TERM

run_with_retries() {
  local description="$1"
  shift
  local max_attempts=3
  local attempt=1

  while true; do
    if "$@"; then
      return 0
    fi

    if (( attempt >= max_attempts )); then
      echo "[kafka-transport] ${description} failed after ${attempt} attempts." >&2
      return 1
    fi

    echo "[kafka-transport] ${description} failed on attempt ${attempt}; retrying..." >&2
    docker compose down --volumes --remove-orphans >/dev/null 2>&1 || true
    sleep $(( attempt * 5 ))
    attempt=$(( attempt + 1 ))
  done
}

echo "[kafka-transport][1/3] Restore and build..."
bash tools/ci/restore_and_build.sh

echo "[kafka-transport][2/3] Start Kafka..."
acquire_distributed_smoke_lock "${LOCK_OWNER}"
ensure_local_tcp_ports_free "${LOCK_OWNER}" "${KAFKA_PORT}"
run_with_retries "docker compose up kafka" docker compose up -d kafka
wait_kafka_health "${KAFKA_CONTAINER}"

echo "[kafka-transport][3/3] Run distributed Kafka integration test..."
AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS="localhost:${KAFKA_PORT}" \
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj \
  --nologo \
  --tl:off \
  -m:1 \
  -p:UseSharedCompilation=false \
  -p:NuGetAudit=false \
  --no-restore \
  --filter "FullyQualifiedName~OrleansMassTransitRuntimeIntegrationTests.KafkaTransport_ShouldDeliverEnvelopeToRuntimeActorGrain"

echo "[kafka-transport] Smoke test passed."
