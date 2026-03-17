#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${ROOT_DIR}"

docker compose \
  -f docker-compose.yml \
  -f docker-compose.projection-providers.yml \
  -f docker-compose.mainnet-cluster.yml \
  down
