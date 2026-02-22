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

echo "Running solution split test guards..."

args=(
  --nologo
  --tl:off
  -m:1
  -p:UseSharedCompilation=false
  -p:NuGetAudit=false
)

if [[ "${SPLIT_TEST_NO_RESTORE:-0}" == "1" ]]; then
  args+=(--no-restore)
fi

if [[ "${SPLIT_TEST_NO_BUILD:-0}" == "1" ]]; then
  args+=(--no-build)
fi

for filter in "${filters[@]}"; do
  if [ ! -f "${filter}" ]; then
    echo "Missing solution filter: ${filter}"
    exit 1
  fi

  echo "Testing ${filter}"
  dotnet test "${filter}" "${args[@]}"
done

echo "Solution split test guards passed."
