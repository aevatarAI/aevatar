#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

echo "[event-sourcing][1/4] Build foundation slice..."
dotnet build aevatar.foundation.slnf \
  --nologo \
  --tl:off \
  -m:1 \
  -p:UseSharedCompilation=false \
  -p:NuGetAudit=false

echo "[event-sourcing][2/4] Run EventSourcing core tests..."
dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj \
  --nologo \
  --tl:off \
  -m:1 \
  -p:UseSharedCompilation=false \
  -p:NuGetAudit=false \
  --filter "FullyQualifiedName~EventSourcing"

echo "[event-sourcing][3/4] Run Orleans + Garnet persistence smoke..."
bash tools/ci/orleans_garnet_persistence_smoke.sh

echo "[event-sourcing][4/4] Run architecture guards..."
bash tools/ci/architecture_guards.sh

echo "[event-sourcing] Regression suite passed."
