#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

dotnet restore aevatar.slnx --nologo

dotnet build aevatar.slnx \
  --nologo \
  --no-restore \
  --tl:off \
  -m:1 \
  -p:UseSharedCompilation=false \
  -p:NuGetAudit=false \
  -warnaserror:CA1501,CA1502,CA1505,CA1506,CA1509
