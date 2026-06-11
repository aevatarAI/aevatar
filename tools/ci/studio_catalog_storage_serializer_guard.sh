#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

actor_backed_store_files=(
  "src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedConnectorCatalogStore.cs"
  "src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedRoleCatalogStore.cs"
)

local_import_reader_file="src/Aevatar.Studio.Infrastructure/Storage/StudioLocalCatalogImportReader.cs"

import_parser_files=(
  "src/Aevatar.Studio.Infrastructure/Storage/ConnectorCatalogImportParser.cs"
  "src/Aevatar.Studio.Infrastructure/Storage/RoleCatalogImportParser.cs"
)

if rg -n "System\.Text\.Json|JsonDocument|JsonSerializer|JsonNode|JsonValueKind|IsJsonPayload|Newtonsoft" "${actor_backed_store_files[@]}" "${local_import_reader_file}"; then
  echo "Studio catalog actor-backed production paths must not parse or serialize JSON. Keep JSON only in explicit local import parsers."
  exit 1
fi

if rg -n "File\.(Open|OpenRead|Read|ReadAll|Write|WriteAll)|ChronoStorageCatalogBlobClient|UploadAsync|TryDownloadAsync|connectors\.json|roles\.json" "${actor_backed_store_files[@]}"; then
  echo "Studio catalog actor-backed stores must use actor commands + projected read models, not file/blob catalog persistence."
  exit 1
fi

required_patterns=(
  "ActorBackedConnectorCatalogStore.cs:ConnectorCatalogSavedEvent"
  "ActorBackedConnectorCatalogStore.cs:ConnectorDraftSavedEvent"
  "ActorBackedConnectorCatalogStore.cs:ConnectorDraftDeletedEvent"
  "ActorBackedConnectorCatalogStore.cs:Unpack<ConnectorCatalogState>"
  "ActorBackedRoleCatalogStore.cs:RoleCatalogSavedEvent"
  "ActorBackedRoleCatalogStore.cs:RoleDraftSavedEvent"
  "ActorBackedRoleCatalogStore.cs:RoleDraftDeletedEvent"
  "ActorBackedRoleCatalogStore.cs:Unpack<RoleCatalogState>"
  "StudioLocalCatalogImportReader.cs:IConnectorCatalogImportParser"
  "StudioLocalCatalogImportReader.cs:IRoleCatalogImportParser"
  "ConnectorCatalogImportParser.cs:JsonDocument.ParseAsync"
  "RoleCatalogImportParser.cs:JsonDocument.ParseAsync"
)

for required in "${required_patterns[@]}"; do
  file_name="${required%%:*}"
  pattern="${required#*:}"
  case "${file_name}" in
    ActorBackedConnectorCatalogStore.cs)
      file_path="${actor_backed_store_files[0]}"
      ;;
    ActorBackedRoleCatalogStore.cs)
      file_path="${actor_backed_store_files[1]}"
      ;;
    StudioLocalCatalogImportReader.cs)
      file_path="${local_import_reader_file}"
      ;;
    ConnectorCatalogImportParser.cs)
      file_path="${import_parser_files[0]}"
      ;;
    RoleCatalogImportParser.cs)
      file_path="${import_parser_files[1]}"
      ;;
    *)
      echo "Unknown studio catalog guard file '${file_name}'."
      exit 1
      ;;
  esac

  if ! rg -q "${pattern}" "${file_path}"; then
    echo "Studio catalog production path guard expected '${pattern}' in ${file_path}."
    exit 1
  fi
done

echo "studio_catalog_storage_serializer_guard: ok"
