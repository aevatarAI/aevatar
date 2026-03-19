#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"
source "${SCRIPT_DIR}/distributed_smoke_common.sh"

KAFKA_CONTAINER="${AEVATAR_DISTRIBUTED_SMOKE_KAFKA_CONTAINER:-aevatar-kafka}"
KAFKA_BOOTSTRAP_SERVERS="${AEVATAR_DISTRIBUTED_SMOKE_KAFKA_BOOTSTRAP_SERVERS:-localhost:9092}"
KAFKA_HOST="${AEVATAR_DISTRIBUTED_SMOKE_KAFKA_HOST:-127.0.0.1}"
KAFKA_PORT="${AEVATAR_DISTRIBUTED_SMOKE_KAFKA_PORT:-9092}"
GARNET_HOST="${AEVATAR_DISTRIBUTED_SMOKE_GARNET_HOST:-127.0.0.1}"
GARNET_PORT="${AEVATAR_DISTRIBUTED_SMOKE_GARNET_PORT:-6379}"
STREAM_BACKEND="${AEVATAR_DISTRIBUTED_SMOKE_STREAM_BACKEND:-KafkaStrictProvider}"
BOOTSTRAP_DOCKER_INFRA="${AEVATAR_DISTRIBUTED_SMOKE_BOOTSTRAP_DOCKER_INFRA:-true}"
PUBLISH_DIR="/tmp/aevatar-mainnet-publish"
APP_DLL="${PUBLISH_DIR}/Aevatar.Mainnet.Host.Api.dll"
WAIT_SECONDS=120

HTTP_PORTS=(18081 18082 18083)
SILO_PORTS=(11111 11112 11113)
GATEWAY_PORTS=(30000 30001 30002)
LOCK_OWNER="distributed_3node_smoke"

timestamp="$(date +%Y%m%d-%H%M%S)"
cluster_id="aevatar-mainnet-ci-cluster-${timestamp}"
service_id="aevatar-mainnet-host-api"
log_dir="/tmp/aevatar-distributed-smoke-${timestamp}"
mkdir -p "${log_dir}"

declare -a pids=()
STARTED_DOCKER_INFRA=0

cleanup() {
  for pid in "${pids[@]-}"; do
    if kill -0 "${pid}" 2>/dev/null; then
      kill "${pid}" 2>/dev/null || true
    fi
  done

  sleep 1
  for pid in "${pids[@]-}"; do
    if kill -0 "${pid}" 2>/dev/null; then
      kill -9 "${pid}" 2>/dev/null || true
    fi
  done

  if [[ "${STARTED_DOCKER_INFRA}" == "1" ]]; then
    docker compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi
  release_distributed_smoke_lock
}
trap cleanup EXIT INT TERM

start_node() {
  local node="$1"
  local http_port="$2"
  local silo_port="$3"
  local gateway_port="$4"
  local primary="$5"
  local log_file="${log_dir}/node${node}.log"

  (
    ASPNETCORE_ENVIRONMENT=Distributed \
    ASPNETCORE_URLS="http://127.0.0.1:${http_port}" \
    AEVATAR_ActorRuntime__Provider=Orleans \
    AEVATAR_ActorRuntime__OrleansStreamBackend="${STREAM_BACKEND}" \
    AEVATAR_ActorRuntime__OrleansPersistenceBackend=Garnet \
    AEVATAR_ActorRuntime__OrleansGarnetConnectionString="${GARNET_HOST}:${GARNET_PORT}" \
    AEVATAR_ActorRuntime__KafkaBootstrapServers="${KAFKA_BOOTSTRAP_SERVERS}" \
    AEVATAR_ActorRuntime__KafkaTopicName=aevatar-mainnet-agent-events \
    AEVATAR_ActorRuntime__KafkaConsumerGroup="aevatar-mainnet-ci-node${node}-${timestamp}" \
    AEVATAR_Orleans__ClusteringMode=Development \
    AEVATAR_Orleans__ClusterId="${cluster_id}" \
    AEVATAR_Orleans__ServiceId="${service_id}" \
    AEVATAR_Orleans__SiloHost=127.0.0.1 \
    AEVATAR_Orleans__PrimarySiloEndpoint="${primary}" \
    AEVATAR_Orleans__SiloPort="${silo_port}" \
    AEVATAR_Orleans__GatewayPort="${gateway_port}" \
    AEVATAR_Orleans__ListenOnAnyHostAddress=true \
    dotnet "${APP_DLL}" >"${log_file}" 2>&1
  ) &

  pids+=("$!")
}

echo "Starting Kafka..."
acquire_distributed_smoke_lock "${LOCK_OWNER}"
if [[ "${BOOTSTRAP_DOCKER_INFRA}" == "true" ]]; then
  ensure_local_tcp_ports_free \
    "${LOCK_OWNER}" \
    "${KAFKA_PORT}" \
    "${GARNET_PORT}" \
    "${HTTP_PORTS[@]}" \
    "${SILO_PORTS[@]}" \
    "${GATEWAY_PORTS[@]}"
  docker compose up -d kafka garnet
  STARTED_DOCKER_INFRA=1
  wait_kafka_health "${KAFKA_CONTAINER}"
  wait_tcp_endpoint "${GARNET_HOST}" "${GARNET_PORT}" "Garnet"
else
  ensure_local_tcp_ports_free \
    "${LOCK_OWNER}" \
    "${HTTP_PORTS[@]}" \
    "${SILO_PORTS[@]}" \
    "${GATEWAY_PORTS[@]}"
  wait_tcp_endpoint "${KAFKA_HOST}" "${KAFKA_PORT}" "Kafka"
  wait_tcp_endpoint "${GARNET_HOST}" "${GARNET_PORT}" "Garnet"
fi

echo "Publishing host app..."
dotnet publish src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj \
  -c Release \
  -o "${PUBLISH_DIR}" \
  --nologo \
  --tl:off \
  -m:1 \
  -p:UseSharedCompilation=false \
  -p:NuGetAudit=false

if [[ ! -f "${APP_DLL}" ]]; then
  echo "Published host application not found: ${APP_DLL}"
  exit 1
fi

echo "Starting 3-node cluster..."
start_node 1 "${HTTP_PORTS[0]}" "${SILO_PORTS[0]}" "${GATEWAY_PORTS[0]}" "127.0.0.1:${SILO_PORTS[0]}"
sleep 3
start_node 2 "${HTTP_PORTS[1]}" "${SILO_PORTS[1]}" "${GATEWAY_PORTS[1]}" "127.0.0.1:${SILO_PORTS[0]}"
start_node 3 "${HTTP_PORTS[2]}" "${SILO_PORTS[2]}" "${GATEWAY_PORTS[2]}" "127.0.0.1:${SILO_PORTS[0]}"

echo "Log directory: ${log_dir}"

ready=0
for _ in $(seq 1 "${WAIT_SECONDS}"); do
  healthy=0

  for port in "${HTTP_PORTS[@]}"; do
    code="$(curl --max-time 1 -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:${port}/api/agents" || true)"
    if [[ "${code}" == "200" || "${code}" == "204" ]]; then
      healthy=$((healthy + 1))
    fi
  done

  if [[ "${healthy}" -eq 3 ]]; then
    ready=1
    break
  fi

  sleep 1
done

echo "READY=${ready}"
for port in "${HTTP_PORTS[@]}"; do
  code="$(curl --max-time 1 -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:${port}/api/agents" || true)"
  echo "HTTP_${port}=${code}"
done

for n in 1 2 3; do
  log_file="${log_dir}/node${n}.log"
  echo "NODE${n}_KEYLOGS"
  grep -nE "Duplicate workflow definition name|Loaded [0-9]+ workflow definition|Orleans Silo started|Now listening on|Unhandled exception|Failed" "${log_file}" || true
  echo
done

if [[ "${ready}" -ne 1 ]]; then
  echo "3-node cluster did not become healthy."
  exit 1
fi

for n in 1 2 3; do
  log_file="${log_dir}/node${n}.log"

  if grep -q "Duplicate workflow definition name" "${log_file}"; then
    echo "Node ${n} failed with duplicate workflow definition."
    exit 1
  fi

  if ! grep -q "Orleans Silo started." "${log_file}"; then
    echo "Node ${n} did not start Orleans Silo."
    exit 1
  fi
done

echo "Running distributed cluster consistency integration tests..."
AEVATAR_TEST_CLUSTER_NODE1_BASE_URL="http://127.0.0.1:${HTTP_PORTS[0]}" \
AEVATAR_TEST_CLUSTER_NODE2_BASE_URL="http://127.0.0.1:${HTTP_PORTS[1]}" \
AEVATAR_TEST_CLUSTER_NODE3_BASE_URL="http://127.0.0.1:${HTTP_PORTS[2]}" \
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~DistributedClusterConsistencyIntegrationTests"

echo "Distributed 3-node smoke test passed."
