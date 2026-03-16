#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

legacy_projection_hits="$(
  rg -n "ProjectionEnvelope|ProjectionEnvelopeNormalizer|ProjectionEnvelopeTimestampResolver|ReadModelRoot|read_model_root|ReduceReadModel" \
    src test \
    -g '*.cs' \
    -g '*.proto' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"

if [[ -n "${legacy_projection_hits}" ]]; then
  echo "${legacy_projection_hits}"
  echo "Legacy projection/readmodel semantics are forbidden. Use EventEnvelope<CommittedStateEventPublished>, committed state_root, and actor-side projection contracts."
  exit 1
fi

echo "Committed-state projection guard passed."
