#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

COMPOSE_FILE="docker-compose.projection-providers.yml"
ELASTICSEARCH_ENDPOINT="http://127.0.0.1:9200"
NEO4J_HOST="127.0.0.1"
NEO4J_PORT="7687"
NEO4J_URI="bolt://${NEO4J_HOST}:${NEO4J_PORT}"
NEO4J_USERNAME="neo4j"
NEO4J_PASSWORD="password"
RESULTS_DIR=""
export NEO4J_PASSWORD

cleanup() {
  docker compose -f "${COMPOSE_FILE}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  if [ -n "${RESULTS_DIR}" ] && [ -d "${RESULTS_DIR}" ]; then
    rm -rf "${RESULTS_DIR}" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

run_provider_integration_tests_on_host() {
  local project="$1"
  local log_file="$2"

  dotnet test "${project}" \
    --nologo \
    --filter "Category=ProviderIntegration" \
    --logger "trx;LogFileName=${log_file}" \
    --results-directory "${RESULTS_DIR}"
}

probe_elasticsearch_from_container() {
  docker compose -f "${COMPOSE_FILE}" exec -T elasticsearch bash -lc \
    'exec 3<>/dev/tcp/127.0.0.1/9200; printf "GET /_cluster/health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3; head -n 20 <&3' \
    2>/dev/null || true
}

wait_elasticsearch() {
  for _ in {1..90}; do
    status="$(probe_elasticsearch_from_container | rg -o "\"status\":\"[^\"]+\"" || true)"
    if [[ "${status}" == "\"status\":\"green\"" || "${status}" == "\"status\":\"yellow\"" ]]; then
      echo "Elasticsearch is ready: ${status}"
      return 0
    fi

    echo "Waiting for Elasticsearch on ${ELASTICSEARCH_ENDPOINT}..."
    sleep 2
  done

  echo "Elasticsearch failed to become ready."
  return 1
}

wait_neo4j() {
  for _ in {1..90}; do
    if docker compose -f "${COMPOSE_FILE}" exec -T neo4j bash -lc 'exec 3<>/dev/tcp/127.0.0.1/7687' >/dev/null 2>&1; then
      echo "Neo4j bolt endpoint is reachable on ${NEO4J_HOST}:${NEO4J_PORT}."
      return 0
    fi

    echo "Waiting for Neo4j bolt endpoint on ${NEO4J_HOST}:${NEO4J_PORT}..."
    sleep 2
  done

  echo "Neo4j failed to become reachable."
  return 1
}

host_can_reach_providers() {
  curl --max-time 2 -s "${ELASTICSEARCH_ENDPOINT}/_cluster/health" >/dev/null 2>&1 &&
    bash -lc "exec 3<>/dev/tcp/${NEO4J_HOST}/${NEO4J_PORT}" >/dev/null 2>&1
}

echo "Starting Elasticsearch + Neo4j..."
docker compose -f "${COMPOSE_FILE}" up -d elasticsearch neo4j

wait_elasticsearch
wait_neo4j

echo "Running projection provider integration tests..."
mkdir -p "${REPO_ROOT}/artifacts/ci"
RESULTS_DIR="$(mktemp -d "${REPO_ROOT}/artifacts/ci/projection-provider-e2e.XXXXXX")"
RESULTS_FILE_CORE="${RESULTS_DIR}/projection-provider-core-e2e.trx"
RESULTS_FILE_SCRIPTING="${RESULTS_DIR}/projection-provider-scripting-e2e.trx"

if ! host_can_reach_providers; then
  echo "Projection providers are ready in Docker, but host cannot reach ${ELASTICSEARCH_ENDPOINT} / ${NEO4J_URI}."
  exit 1
fi

echo "Host can reach projection providers; running integration tests from host."
export AEVATAR_TEST_ELASTICSEARCH_ENDPOINT="${ELASTICSEARCH_ENDPOINT}"
export AEVATAR_TEST_NEO4J_URI="${NEO4J_URI}"
export AEVATAR_TEST_NEO4J_USERNAME="${NEO4J_USERNAME}"
export AEVATAR_TEST_NEO4J_PASSWORD="${NEO4J_PASSWORD}"

run_provider_integration_tests_on_host \
  "test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj" \
  "projection-provider-core-e2e.trx"
run_provider_integration_tests_on_host \
  "test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj" \
  "projection-provider-scripting-e2e.trx"

validate_trx() {
  local file="$1"
  local label="$2"

  if [ ! -f "${file}" ]; then
    echo "${label} trx result is missing: ${file}"
    exit 1
  fi

  local total
  local executed
  local not_executed
  total="$(grep -o 'total=\"[0-9]*\"' "${file}" | head -n1 | awk -F'"' '{print $2}')"
  executed="$(grep -o 'executed=\"[0-9]*\"' "${file}" | head -n1 | awk -F'"' '{print $2}')"
  not_executed="$(grep -o 'notExecuted=\"[0-9]*\"' "${file}" | head -n1 | awk -F'"' '{print $2}')"

  if [ -z "${total}" ] || [ -z "${executed}" ] || [ -z "${not_executed}" ]; then
    echo "Failed to parse test counters from ${file}."
    exit 1
  fi

  if [ "${not_executed}" -ne 0 ] || [ "${executed}" -ne "${total}" ]; then
    echo "${label} tests were not fully executed. total=${total} executed=${executed} notExecuted=${not_executed}"
    exit 1
  fi

  echo "${label} tests executed fully. total=${total} executed=${executed}."
}

validate_trx "${RESULTS_FILE_CORE}" "Projection provider core e2e"
validate_trx "${RESULTS_FILE_SCRIPTING}" "Projection provider scripting e2e"

echo "Projection provider e2e smoke test passed."
