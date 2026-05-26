#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

if ! command -v buf >/dev/null 2>&1; then
  echo "buf is required to lint proto contracts."
  exit 1
fi

echo "Running proto lint guard (buf lint)..."
buf lint
