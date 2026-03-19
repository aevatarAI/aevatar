#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"
source "${SCRIPT_DIR}/distributed_smoke_common.sh"

COMPOSE_ARGS=(-f docker-compose.yml -f docker-compose.projection-providers.yml)
KAFKA_CONTAINER="${AEVATAR_DISTRIBUTED_SMOKE_KAFKA_CONTAINER:-aevatar-kafka}"
KAFKA_BOOTSTRAP_SERVERS="${AEVATAR_DISTRIBUTED_SMOKE_KAFKA_BOOTSTRAP_SERVERS:-localhost:9092}"
KAFKA_HOST="${AEVATAR_DISTRIBUTED_SMOKE_KAFKA_HOST:-127.0.0.1}"
KAFKA_PORT="${AEVATAR_DISTRIBUTED_SMOKE_KAFKA_PORT:-9092}"
GARNET_HOST="${AEVATAR_DISTRIBUTED_SMOKE_GARNET_HOST:-127.0.0.1}"
GARNET_PORT="${AEVATAR_DISTRIBUTED_SMOKE_GARNET_PORT:-6379}"
ELASTICSEARCH_ENDPOINT="${AEVATAR_DISTRIBUTED_SMOKE_ELASTICSEARCH_ENDPOINT:-http://127.0.0.1:9200}"
ELASTICSEARCH_PORT="${AEVATAR_DISTRIBUTED_SMOKE_ELASTICSEARCH_PORT:-9200}"
ELASTICSEARCH_TRANSPORT_PORT="${AEVATAR_DISTRIBUTED_SMOKE_ELASTICSEARCH_TRANSPORT_PORT:-9300}"
NEO4J_URI="${AEVATAR_DISTRIBUTED_SMOKE_NEO4J_URI:-bolt://127.0.0.1:7687}"
NEO4J_USERNAME="${AEVATAR_DISTRIBUTED_SMOKE_NEO4J_USERNAME:-neo4j}"
NEO4J_PASSWORD="${AEVATAR_DISTRIBUTED_SMOKE_NEO4J_PASSWORD:-password}"
NEO4J_HTTP_PORT="${AEVATAR_DISTRIBUTED_SMOKE_NEO4J_HTTP_PORT:-7474}"
NEO4J_BOLT_HOST="${AEVATAR_DISTRIBUTED_SMOKE_NEO4J_BOLT_HOST:-127.0.0.1}"
NEO4J_BOLT_PORT="${AEVATAR_DISTRIBUTED_SMOKE_NEO4J_BOLT_PORT:-7687}"
STREAM_BACKEND="${AEVATAR_DISTRIBUTED_SMOKE_STREAM_BACKEND:-KafkaStrictProvider}"
BOOTSTRAP_DOCKER_INFRA="${AEVATAR_DISTRIBUTED_SMOKE_BOOTSTRAP_DOCKER_INFRA:-true}"
PUBLISH_DIR="/tmp/aevatar-mainnet-publish"
DEFAULT_APP_DLL="${PUBLISH_DIR}/Aevatar.Mainnet.Host.Api.dll"
WAIT_SECONDS="${WAIT_SECONDS:-180}"
OLD_NODE_COUNT="${OLD_NODE_COUNT:-3}"
NEW_NODE_COUNT="${NEW_NODE_COUNT:-3}"
MIXED_FAIL_EVENT_TYPE_URLS="${MIXED_FAIL_EVENT_TYPE_URLS:-}"
MIXED_EVENT_PROBE_ENABLED="${MIXED_EVENT_PROBE_ENABLED:-true}"

HTTP_PORTS=(18081 18082 18083 18084 18085 18086)
SILO_PORTS=(21111 21112 21113 21114 21115 21116)
GATEWAY_PORTS=(31000 31001 31002 31003 31004 31005)
LOCK_OWNER="distributed_mixed_version_smoke"

timestamp="$(date +%Y%m%d-%H%M%S)"
cluster_id="aevatar-mainnet-mixed-ci-cluster-${timestamp}"
service_id="aevatar-mainnet-host-api"
log_dir="/tmp/aevatar-mixed-distributed-smoke-${timestamp}"
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
    docker compose "${COMPOSE_ARGS[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  fi
  release_distributed_smoke_lock
}
trap cleanup EXIT INT TERM

publish_default_app() {
  echo "Publishing host app for default old/new binary..." >&2
  dotnet publish src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj \
    -c Release \
    -o "${PUBLISH_DIR}" \
    --nologo \
    --tl:off \
    -m:1 \
    -p:UseSharedCompilation=false \
    -p:NuGetAudit=false \
    >&2
}

resolve_app_dlls() {
  local old_app_dll="${MIXED_OLD_APP_DLL:-}"
  local new_app_dll="${MIXED_NEW_APP_DLL:-}"

  if [[ -z "${old_app_dll}" || -z "${new_app_dll}" ]]; then
    publish_default_app
    old_app_dll="${old_app_dll:-${DEFAULT_APP_DLL}}"
    new_app_dll="${new_app_dll:-${DEFAULT_APP_DLL}}"
  fi

  if [[ ! -f "${old_app_dll}" ]]; then
    echo "Old app dll not found: ${old_app_dll}"
    exit 1
  fi

  if [[ ! -f "${new_app_dll}" ]]; then
    echo "New app dll not found: ${new_app_dll}"
    exit 1
  fi

  echo "${old_app_dll}|${new_app_dll}"
}

start_node() {
  local node="$1"
  local http_port="$2"
  local silo_port="$3"
  local gateway_port="$4"
  local primary="$5"
  local app_dll="$6"
  local version_tag="$7"
  local fail_event_type_urls="$8"
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
    AEVATAR_ActorRuntime__KafkaConsumerGroup="aevatar-mainnet-mixed-ci-group" \
    AEVATAR_Orleans__ClusteringMode=Development \
    AEVATAR_Orleans__ClusterId="${cluster_id}" \
    AEVATAR_Orleans__ServiceId="${service_id}" \
    AEVATAR_Orleans__SiloHost=127.0.0.1 \
    AEVATAR_Orleans__PrimarySiloEndpoint="${primary}" \
    AEVATAR_Orleans__SiloPort="${silo_port}" \
    AEVATAR_Orleans__GatewayPort="${gateway_port}" \
    AEVATAR_Orleans__ListenOnAnyHostAddress=true \
    Projection__Document__Providers__Elasticsearch__Enabled=true \
    Projection__Document__Providers__Elasticsearch__Endpoints__0="${ELASTICSEARCH_ENDPOINT}" \
    Projection__Document__Providers__Elasticsearch__IndexPrefix="aevatar-mainnet-mixed-ci-${timestamp}" \
    Projection__Document__Providers__InMemory__Enabled=false \
    Projection__Graph__Providers__Neo4j__Enabled=true \
    Projection__Graph__Providers__Neo4j__Uri="${NEO4J_URI}" \
    Projection__Graph__Providers__Neo4j__Username="${NEO4J_USERNAME}" \
    Projection__Graph__Providers__Neo4j__Password="${NEO4J_PASSWORD}" \
    Projection__Graph__Providers__InMemory__Enabled=false \
    AEVATAR_TEST_NODE_VERSION_TAG="${version_tag}" \
    AEVATAR_TEST_FAIL_EVENT_TYPE_URLS="${fail_event_type_urls}" \
    dotnet "${app_dll}" >"${log_file}" 2>&1
  ) &

  pids+=("$!")
}

probe_event_path() {
  local old_node_port="$1"
  local workflow_payload
  local workflow_name
  local chat_status
  local probe_log_file="${log_dir}/event-probe-response.log"

  if [[ "${MIXED_EVENT_PROBE_ENABLED}" != "true" ]]; then
    echo "Event probe is disabled."
    return 0
  fi

  echo "Running event-path probe against old node on port ${old_node_port}..."
  workflow_payload="$(curl --max-time 3 -sS "http://127.0.0.1:${old_node_port}/api/workflows" || true)"
  if [[ -z "${workflow_payload}" ]]; then
    echo "Unable to query workflows from old node."
    return 1
  fi

  workflow_name="$(WORKFLOW_JSON="${workflow_payload}" python3 - <<'PY'
import json
import os

raw = os.environ.get("WORKFLOW_JSON", "")
try:
    data = json.loads(raw)
except Exception:
    print("")
    raise SystemExit(0)

if isinstance(data, list) and data:
    first = data[0]
    if isinstance(first, str):
        print(first)
    else:
        print("")
else:
    print("")
PY
)"

  if [[ -z "${workflow_name}" ]]; then
    echo "No workflow available for event-path probe."
    return 1
  fi

  chat_status="$(
    OLD_NODE_PORT="${old_node_port}" \
    WORKFLOW_NAME="${workflow_name}" \
    PROBE_LOG_FILE="${probe_log_file}" \
    python3 - <<'PY'
import json
import os
import socket
import urllib.error
import urllib.request

port = os.environ["OLD_NODE_PORT"]
workflow_name = os.environ["WORKFLOW_NAME"]
probe_log_file = os.environ["PROBE_LOG_FILE"]
payload = json.dumps(
    {
        "prompt": "mixed-version-event-probe",
        "workflow": workflow_name,
    }
).encode("utf-8")
request = urllib.request.Request(
    f"http://127.0.0.1:{port}/api/chat",
    data=payload,
    headers={"Content-Type": "application/json"},
    method="POST",
)

def write_log(content: str) -> None:
    with open(probe_log_file, "w", encoding="utf-8") as handle:
        handle.write(content)

try:
    with urllib.request.urlopen(request, timeout=40) as response:
        with open(probe_log_file, "w", encoding="utf-8") as handle:
            while True:
                line = response.readline()
                if not line:
                    break

                text = line.decode("utf-8", errors="replace")
                handle.write(text)
                handle.flush()

                if text.startswith("data:"):
                    break

        print(response.status)
except urllib.error.HTTPError as error:
    write_log(error.read().decode("utf-8", errors="replace"))
    print(error.code)
except (urllib.error.URLError, TimeoutError, socket.timeout):
    write_log("")
    print("000")
PY
  )"

  echo "Event-path probe HTTP status: ${chat_status}"

  if [[ "${chat_status}" != "200" ]]; then
    echo "Event-path probe returned unexpected status: ${chat_status}"
    return 1
  fi

  if ! grep -q "POST http://127.0.0.1:${old_node_port}/api/chat" "${log_dir}/node1.log"; then
    echo "Event-path probe did not reach old-node /api/chat endpoint."
    return 1
  fi

  if ! grep -q '^data:' "${probe_log_file}"; then
    echo "Event-path probe did not receive an SSE data frame."
    return 1
  fi

  if ! grep -q '"name": "aevatar.run.context"' "${probe_log_file}"; then
    echo "Event-path probe did not receive the expected run-context SSE frame."
    return 1
  fi

  echo "Observed initial SSE run-context frame from old node."

  if [[ -n "${MIXED_FAIL_EVENT_TYPE_URLS}" ]]; then
    local observed_failure=0
    local observed_runtime_retry=0
    for ((old_node=1; old_node<=OLD_NODE_COUNT; old_node++)); do
      if grep -q "Injected compatibility failure" "${log_dir}/node${old_node}.log"; then
        observed_failure=1
      fi
      if grep -q "Runtime envelope retry scheduled" "${log_dir}/node${old_node}.log"; then
        observed_runtime_retry=1
      fi
    done

    if [[ "${observed_failure}" -ne 1 ]]; then
      echo "Injected compatibility failure was not observed in old-node logs."
      return 1
    fi
    echo "Observed injected compatibility failure in old-node logs."

    if [[ "${observed_runtime_retry}" -ne 1 ]]; then
      echo "Runtime retry scheduling log was not observed in old-node logs."
      return 1
    fi
    echo "Observed runtime retry scheduling in old-node logs."
  fi

  return 0
}

echo "Starting Kafka, Garnet, Elasticsearch and Neo4j..."
total_nodes=$((OLD_NODE_COUNT + NEW_NODE_COUNT))
if (( total_nodes > 6 || total_nodes < 2 )); then
  echo "Unsupported node count: old(${OLD_NODE_COUNT}) + new(${NEW_NODE_COUNT}) must be between 2 and 6."
  exit 1
fi

acquire_distributed_smoke_lock "${LOCK_OWNER}"
if [[ "${BOOTSTRAP_DOCKER_INFRA}" == "true" ]]; then
  ensure_local_tcp_ports_free \
    "${LOCK_OWNER}" \
    "${KAFKA_PORT}" \
    "${GARNET_PORT}" \
    "${ELASTICSEARCH_PORT}" \
    "${ELASTICSEARCH_TRANSPORT_PORT}" \
    "${NEO4J_HTTP_PORT}" \
    "${NEO4J_BOLT_PORT}" \
    "${HTTP_PORTS[@]:0:${total_nodes}}" \
    "${SILO_PORTS[@]:0:${total_nodes}}" \
    "${GATEWAY_PORTS[@]:0:${total_nodes}}"
  docker compose "${COMPOSE_ARGS[@]}" up -d kafka garnet elasticsearch neo4j
  STARTED_DOCKER_INFRA=1
  wait_kafka_health "${KAFKA_CONTAINER}"
  wait_garnet_health "${GARNET_HOST}" "${GARNET_PORT}"
  wait_elasticsearch_health "${ELASTICSEARCH_ENDPOINT}"
  wait_neo4j_bolt "${NEO4J_BOLT_HOST}" "${NEO4J_BOLT_PORT}"
else
  ensure_local_tcp_ports_free \
    "${LOCK_OWNER}" \
    "${HTTP_PORTS[@]:0:${total_nodes}}" \
    "${SILO_PORTS[@]:0:${total_nodes}}" \
    "${GATEWAY_PORTS[@]:0:${total_nodes}}"
  wait_tcp_endpoint "${KAFKA_HOST}" "${KAFKA_PORT}" "Kafka"
  wait_tcp_endpoint "${GARNET_HOST}" "${GARNET_PORT}" "Garnet"
  wait_elasticsearch_health "${ELASTICSEARCH_ENDPOINT}"
  wait_neo4j_bolt "${NEO4J_BOLT_HOST}" "${NEO4J_BOLT_PORT}"
fi

dll_spec="$(resolve_app_dlls)"
old_app_dll="${dll_spec%%|*}"
new_app_dll="${dll_spec##*|}"

echo "Old app dll: ${old_app_dll}"
echo "New app dll: ${new_app_dll}"
echo "Starting mixed cluster: old=${OLD_NODE_COUNT}, new=${NEW_NODE_COUNT}"
if [[ -n "${MIXED_FAIL_EVENT_TYPE_URLS}" ]]; then
  echo "Old-node compatibility failure injection enabled for event types: ${MIXED_FAIL_EVENT_TYPE_URLS}"
fi

start_node \
  1 \
  "${HTTP_PORTS[0]}" \
  "${SILO_PORTS[0]}" \
  "${GATEWAY_PORTS[0]}" \
  "127.0.0.1:${SILO_PORTS[0]}" \
  "${old_app_dll}" \
  "old" \
  "${MIXED_FAIL_EVENT_TYPE_URLS}"
sleep 3

for ((node=2; node<=total_nodes; node++)); do
  index=$((node - 1))
  if (( node <= OLD_NODE_COUNT )); then
    node_dll="${old_app_dll}"
    version_tag="old"
    fail_event_type_urls="${MIXED_FAIL_EVENT_TYPE_URLS}"
  else
    node_dll="${new_app_dll}"
    version_tag="new"
    fail_event_type_urls=""
  fi

  start_node \
    "${node}" \
    "${HTTP_PORTS[$index]}" \
    "${SILO_PORTS[$index]}" \
    "${GATEWAY_PORTS[$index]}" \
    "127.0.0.1:${SILO_PORTS[0]}" \
    "${node_dll}" \
    "${version_tag}" \
    "${fail_event_type_urls}"
done

echo "Log directory: ${log_dir}"

ready=0
for _ in $(seq 1 "${WAIT_SECONDS}"); do
  healthy=0
  for ((node=1; node<=total_nodes; node++)); do
    index=$((node - 1))
    port="${HTTP_PORTS[$index]}"
    code="$(curl --max-time 1 -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:${port}/api/agents" || true)"
    if [[ "${code}" == "200" || "${code}" == "204" ]]; then
      healthy=$((healthy + 1))
    fi
  done

  if (( healthy == total_nodes )); then
    ready=1
    break
  fi

  sleep 1
done

echo "READY=${ready}"
for ((node=1; node<=total_nodes; node++)); do
  index=$((node - 1))
  port="${HTTP_PORTS[$index]}"
  code="$(curl --max-time 1 -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:${port}/api/agents" || true)"
  echo "HTTP_node${node}_${port}=${code}"
done

for ((node=1; node<=total_nodes; node++)); do
  log_file="${log_dir}/node${node}.log"
  echo "NODE${node}_KEYLOGS"
  grep -nE "Orleans Silo started|Now listening on|Unhandled exception|Failed" "${log_file}" || true
  echo
done

if [[ "${ready}" -ne 1 ]]; then
  echo "Mixed-version cluster did not become healthy."
  exit 1
fi

for ((node=1; node<=total_nodes; node++)); do
  log_file="${log_dir}/node${node}.log"
  if ! grep -q "Orleans Silo started." "${log_file}"; then
    echo "Node ${node} did not start Orleans Silo."
    exit 1
  fi
done

probe_event_path "${HTTP_PORTS[0]}"

for ((node=1; node<=total_nodes; node++)); do
  index=$((node - 1))
  export "AEVATAR_TEST_CLUSTER_NODE${node}_BASE_URL=http://127.0.0.1:${HTTP_PORTS[$index]}"
done
export AEVATAR_TEST_CLUSTER_EXPECTED_NODE_COUNT="${total_nodes}"

echo "Running mixed-version distributed integration tests..."
test_filter="FullyQualifiedName~DistributedClusterConsistencyIntegrationTests|FullyQualifiedName~DistributedMixedVersionClusterIntegrationTests"
if [[ "${MIXED_EVENT_PROBE_ENABLED}" == "true" ]]; then
  test_filter="FullyQualifiedName~WorkflowsEndpoint_ShouldReturnConsistentWorkflowSetAcrossAllNodes|FullyQualifiedName~WorkflowsEndpoint_ShouldBeReachableAcrossConfiguredMixedNodes"
fi

dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj \
  --nologo \
  --filter "${test_filter}"

echo "Distributed mixed-version smoke test passed."
