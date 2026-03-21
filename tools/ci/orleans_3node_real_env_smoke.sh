#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"
source "${SCRIPT_DIR}/distributed_smoke_common.sh"

COMPOSE_ARGS=(-f docker-compose.yml -f docker-compose.projection-providers.yml)
KAFKA_CONTAINER="aevatar-kafka"
GARNET_HOST="127.0.0.1"
GARNET_PORT=6379
ELASTICSEARCH_ENDPOINT="http://127.0.0.1:9200"
ELASTICSEARCH_PORT=9200
ELASTICSEARCH_TRANSPORT_PORT=9300
NEO4J_URI="bolt://127.0.0.1:7687"
NEO4J_USERNAME="neo4j"
NEO4J_PASSWORD="password"
NEO4J_HTTP_PORT=7474
NEO4J_BOLT_PORT=7687
LOCK_OWNER="orleans_3node_real_env_smoke"

cleanup() {
  docker compose "${COMPOSE_ARGS[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  release_distributed_smoke_lock
}
trap cleanup EXIT INT TERM

echo "Starting Kafka, Garnet, Elasticsearch and Neo4j for Orleans 3-node cluster test..."
acquire_distributed_smoke_lock "${LOCK_OWNER}"
ensure_local_tcp_ports_free \
  "${LOCK_OWNER}" \
  9092 \
  "${GARNET_PORT}" \
  "${ELASTICSEARCH_PORT}" \
  "${ELASTICSEARCH_TRANSPORT_PORT}" \
  "${NEO4J_HTTP_PORT}" \
  "${NEO4J_BOLT_PORT}"

docker compose "${COMPOSE_ARGS[@]}" up -d kafka garnet elasticsearch neo4j
wait_kafka_health "${KAFKA_CONTAINER}"
wait_garnet_health "${GARNET_HOST}" "${GARNET_PORT}"
wait_elasticsearch_health "${ELASTICSEARCH_ENDPOINT}"
wait_neo4j_bolt "127.0.0.1" "${NEO4J_BOLT_PORT}"

echo "Running Orleans 3-node scripting cluster integration test..."
AEVATAR_TEST_ORLEANS_3NODE=1 \
AEVATAR_TEST_KAFKA_BOOTSTRAP_SERVERS="127.0.0.1:9092" \
AEVATAR_TEST_GARNET_CONNECTION_STRING="${GARNET_HOST}:${GARNET_PORT}" \
AEVATAR_TEST_ELASTICSEARCH_ENDPOINT="${ELASTICSEARCH_ENDPOINT}" \
AEVATAR_TEST_NEO4J_URI="${NEO4J_URI}" \
AEVATAR_TEST_NEO4J_USERNAME="${NEO4J_USERNAME}" \
AEVATAR_TEST_NEO4J_PASSWORD="${NEO4J_PASSWORD}" \
dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~ScriptAutonomousEvolutionOrleans3ClusterConsistencyTests"

echo "Orleans 3-node real-environment smoke test passed."
