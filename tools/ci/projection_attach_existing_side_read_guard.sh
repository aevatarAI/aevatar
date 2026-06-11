#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Refactor (iter51/issue-898-projection-attach-existing-side-read):
#   Old pattern: Feature projection ports duplicated IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build()) for attach-existing checks (post-#884 #884 fixed 3 ports but more remained).
#   New principle: All attach-existing lease lookups go through typed IProjectionScopeAttachExistingLeaseLookup<TLease>; CI guard prevents recurrence.
set +e
report="$(
  rg -n "IActorRuntime\.ExistsAsync\(ProjectionScopeActorId\.Build|\.ExistsAsync\(ProjectionScopeActorId\.Build" \
    agents src \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    -g '!*.g.cs' \
    -g '!*.Designer.cs' \
    | awk -F: '
BEGIN {
  allowed["src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeActorRuntime.cs"] = 1;
  allowed["src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeAttachExistingLeaseLookup.cs"] = 1;
}

{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  if (file in allowed)
    next;
  if (text ~ /^[[:space:]]*\/\/\/?/)
    next;

  print $0;
}'
)"
status=$?
set -e

if [[ ${status} -ne 0 && ${status} -ne 1 ]]; then
  echo "projection_attach_existing_side_read_guard: scan failed"
  exit "${status}"
fi

if [ -n "${report}" ]; then
  echo "${report}"
  echo "Attach-existing projection ports must use IProjectionScopeAttachExistingLeaseLookup<TLease>; do not duplicate IActorRuntime.ExistsAsync(ProjectionScopeActorId.Build(...)) side reads."
  exit 1
fi

echo "projection_attach_existing_side_read_guard: ok"
