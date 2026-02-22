 #!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${ROOT_DIR}"

docker compose \
  -f docker-compose.yml \
  -f docker-compose.mainnet-cluster.yml \
  up -d --build

echo
echo "Mainnet cluster is starting. API endpoints:"
echo "  node1: http://localhost:19081/"
echo "  node2: http://localhost:19082/"
echo "  node3: http://localhost:19083/"
echo
docker compose \
  -f docker-compose.yml \
  -f docker-compose.mainnet-cluster.yml \
  ps
