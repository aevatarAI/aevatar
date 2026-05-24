#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

serializer_files=(
  "src/Aevatar.Studio.Infrastructure/Storage/ConnectorCatalogStorageSerializer.cs"
  "src/Aevatar.Studio.Infrastructure/Storage/RoleCatalogStorageSerializer.cs"
)

if rg -n "System\.Text\.Json|JsonDocument\.Parse|JsonDocument\.ParseAsync|IsJsonPayload|JsonValueKind" "${serializer_files[@]}"; then
  echo "Studio catalog storage serializers must parse protobuf payloads only. Move JSON compatibility to explicit import/migration readers."
  exit 1
fi

echo "studio_catalog_storage_serializer_guard: ok"
