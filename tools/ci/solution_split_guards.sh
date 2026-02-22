#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "${repo_root}"

filters=(
  "aevatar.foundation.slnf"
  "aevatar.ai.slnf"
  "aevatar.cqrs.slnf"
  "aevatar.workflow.slnf"
  "aevatar.hosting.slnf"
  "aevatar.distributed.slnf"
)

echo "Running solution split guards..."

for filter in "${filters[@]}"; do
  if [ ! -f "${filter}" ]; then
    echo "Missing solution filter: ${filter}"
    exit 1
  fi

  echo "Building ${filter}"
  dotnet build "${filter}" \
    --nologo \
    --no-restore \
    --tl:off \
    -m:1 \
    -p:UseSharedCompilation=false \
    -p:NuGetAudit=false
done

echo "Solution split guards passed."
