#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "${repo_root}"

echo "Running slow test guards..."

args=(
  --nologo
  --tl:off
  -m:1
  -p:UseSharedCompilation=false
  -p:NuGetAudit=false
)

if [[ "${SLOW_TEST_NO_RESTORE:-0}" == "1" ]]; then
  args+=(--no-restore)
fi

if [[ "${SLOW_TEST_NO_BUILD:-0}" == "1" ]]; then
  args+=(--no-build)
fi

dotnet test "test/Aevatar.Integration.Slow.Tests/Aevatar.Integration.Slow.Tests.csproj" "${args[@]}"

echo "Slow test guards passed."
