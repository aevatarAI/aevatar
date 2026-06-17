#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

dotnet test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~WorkflowSagaCompensation"
