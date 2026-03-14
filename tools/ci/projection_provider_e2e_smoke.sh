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

cleanup() {
  docker compose -f "${COMPOSE_FILE}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  if [ -n "${RESULTS_DIR}" ] && [ -d "${RESULTS_DIR}" ]; then
    rm -rf "${RESULTS_DIR}" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

run_provider_integration_tests() {
  local project="$1"
  local log_file="$2"

  dotnet test "${project}" \
    --nologo \
    --filter "Category=ProviderIntegration" \
    --logger "trx;LogFileName=${log_file}" \
    --results-directory "${RESULTS_DIR}"
}

wait_elasticsearch() {
  for _ in {1..90}; do
    status="$(curl --max-time 2 -s "${ELASTICSEARCH_ENDPOINT}/_cluster/health" | rg -o "\"status\":\"[^\"]+\"" || true)"
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
    if (echo >"/dev/tcp/${NEO4J_HOST}/${NEO4J_PORT}") >/dev/null 2>&1; then
      echo "Neo4j bolt endpoint is reachable on ${NEO4J_HOST}:${NEO4J_PORT}."
      return 0
    fi

    echo "Waiting for Neo4j bolt endpoint on ${NEO4J_HOST}:${NEO4J_PORT}..."
    sleep 2
  done

  echo "Neo4j failed to become reachable."
  return 1
}

echo "Starting Elasticsearch + Neo4j..."
docker compose -f "${COMPOSE_FILE}" up -d elasticsearch neo4j

wait_elasticsearch
wait_neo4j

echo "Running projection provider integration tests..."
RESULTS_DIR="$(mktemp -d)"
RESULTS_FILE_CORE="${RESULTS_DIR}/projection-provider-core-e2e.trx"
RESULTS_FILE_SCRIPTING="${RESULTS_DIR}/projection-provider-scripting-e2e.trx"
export AEVATAR_TEST_ELASTICSEARCH_ENDPOINT="${ELASTICSEARCH_ENDPOINT}"
export AEVATAR_TEST_NEO4J_URI="${NEO4J_URI}"
export AEVATAR_TEST_NEO4J_USERNAME="${NEO4J_USERNAME}"
export AEVATAR_TEST_NEO4J_PASSWORD="${NEO4J_PASSWORD}"

run_provider_integration_tests \
  "test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj" \
  "projection-provider-core-e2e.trx"
run_provider_integration_tests \
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
