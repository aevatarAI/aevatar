#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

hits="$(
  rg -n "IScriptAuthorityProjectionPrimingPort|IProjectionPortActivationService<|IProjectionPortReleaseService<|EnsureActorProjectionAsync|AttachLiveSinkAsync|ReleaseActorProjectionAsync|PrimeAsync" \
    src \
    -g '**/*Query*.cs' \
    -g '**/*ReadPort*.cs' \
    -g '!**/*PrimingPort*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"

if [[ -n "${hits}" ]]; then
  echo "${hits}"
  echo "Query/read paths must not trigger projection priming, activation, or lifecycle control."
  exit 1
fi

echo "Query projection priming guard passed."
