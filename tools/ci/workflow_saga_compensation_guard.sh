#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

output_file="$(mktemp)"
trap 'rm -f "${output_file}"' EXIT

set +e
dotnet test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj \
  --nologo \
  --filter "FullyQualifiedName~WorkflowSagaCompensation" >"${output_file}" 2>&1
dotnet_status=$?
set -e

cat "${output_file}"

if [[ ${dotnet_status} -ne 0 ]]; then
  exit "${dotnet_status}"
fi

if grep -Fq "No test matches the given testcase filter" "${output_file}"; then
  echo "workflow saga compensation guard failed: the Roslyn architecture test filter matched zero tests." >&2
  exit 1
fi
