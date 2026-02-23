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
RESULTS_FILE="${RESULTS_DIR}/projection-provider-e2e.trx"
AEVATAR_TEST_ELASTICSEARCH_ENDPOINT="${ELASTICSEARCH_ENDPOINT}" \
AEVATAR_TEST_NEO4J_URI="${NEO4J_URI}" \
AEVATAR_TEST_NEO4J_USERNAME="${NEO4J_USERNAME}" \
AEVATAR_TEST_NEO4J_PASSWORD="${NEO4J_PASSWORD}" \
dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~ProjectionProviderE2EIntegrationTests" \
  --logger "trx;LogFileName=projection-provider-e2e.trx" \
  --results-directory "${RESULTS_DIR}"

if [ ! -f "${RESULTS_FILE}" ]; then
  echo "Projection provider e2e trx result is missing: ${RESULTS_FILE}"
  exit 1
fi

total="$(grep -o 'total=\"[0-9]*\"' "${RESULTS_FILE}" | head -n1 | awk -F'"' '{print $2}')"
executed="$(grep -o 'executed=\"[0-9]*\"' "${RESULTS_FILE}" | head -n1 | awk -F'"' '{print $2}')"
not_executed="$(grep -o 'notExecuted=\"[0-9]*\"' "${RESULTS_FILE}" | head -n1 | awk -F'"' '{print $2}')"

if [ -z "${total}" ] || [ -z "${executed}" ] || [ -z "${not_executed}" ]; then
  echo "Failed to parse test counters from ${RESULTS_FILE}."
  exit 1
fi

if [ "${not_executed}" -ne 0 ] || [ "${executed}" -ne "${total}" ]; then
  echo "Projection provider e2e tests were not fully executed. total=${total} executed=${executed} notExecuted=${not_executed}"
  exit 1
fi

echo "Projection provider e2e tests executed fully. total=${total} executed=${executed}."

echo "Projection provider e2e smoke test passed."
