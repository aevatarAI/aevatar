#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

if rg -n "EnsureAndAttachLeaseAsync|EnsureActorProjectionAsync|ActivateReadModelAsync" \
  src test \
  -g '*ObservationLifecycle.cs'; then
  echo "command_observation_attach_only_guard: observation lifecycles must attach only to existing projection-owned sessions" >&2
  exit 1
fi

echo "command_observation_attach_only_guard: ok"
