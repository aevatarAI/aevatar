#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/gagent_registry_kind_guard.sh"
MEMBER_CONTRACT="src/Aevatar.Studio.Application.Abstractions/Studio/Contracts/MemberContracts.cs"
LEGACY_MEMBER_CONTRACT="src/Aevatar.Studio.Application/Studio/Contracts/MemberContracts.cs"

if [[ ! -f "${REPO_ROOT}/${MEMBER_CONTRACT}" ]]; then
  echo "Missing Studio member contract expected by the GAgent registry kind guard: ${MEMBER_CONTRACT}"
  exit 1
fi

if ! rg -Fq "\"${MEMBER_CONTRACT}\"" "${GUARD}"; then
  echo "GAgent registry kind guard must scan the current Studio member contract: ${MEMBER_CONTRACT}"
  exit 1
fi

if rg -Fq "\"${LEGACY_MEMBER_CONTRACT}\"" "${GUARD}"; then
  echo "GAgent registry kind guard still scans the removed Studio member contract: ${LEGACY_MEMBER_CONTRACT}"
  exit 1
fi

echo "gagent registry kind guard tests passed"
