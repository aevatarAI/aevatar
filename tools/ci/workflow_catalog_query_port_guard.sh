#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

query_hits="$(
  rg -n "Directory\\.EnumerateFiles|WorkflowParser\\.Parse|AevatarConnectorConfig\\.LoadConnectors|_cacheLock|_workflowFileDiscoveryCache|_parsedWorkflowCache|lock\\s*\\(" \
    src/workflow/Aevatar.Workflow.Application/Queries \
    src/workflow/Aevatar.Workflow.Projection/Workflows \
    -g '*.cs' \
    | rg -v "Refactor \\(iter46/issue-871-workflow-file-catalog-query-port\\)|Old pattern:|New principle:" \
    || true
)"

legacy_port_hits="$(
  rg -n "class FileBackedWorkflowCatalogPort\\s*:\\s*IWorkflowCatalogPort|class FileBackedWorkflowCatalogPort\\s*:\\s*IWorkflowCapabilitiesPort" \
    src/workflow/Aevatar.Workflow.Infrastructure/Workflows/FileBackedWorkflowCatalogPort.cs \
    || true
)"

if [[ -n "${query_hits}${legacy_port_hits}" ]]; then
  if [[ -n "${query_hits}" ]]; then
    echo "${query_hits}"
  fi
  if [[ -n "${legacy_port_hits}" ]]; then
    echo "${legacy_port_hits}"
    echo "FileBackedWorkflowCatalogPort must not be an online workflow catalog/capabilities query source."
  fi
  echo "Workflow catalog/capabilities query ports must only read freshness-bearing readmodels; file discovery/parsing/connector loading belongs to startup/import materialization."
  exit 1
fi

echo "Workflow catalog query port guard passed."
