#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Tool approval wiring guard (#2004):
#
# ToolApprovalDecision.Yield carries an implicit contract: the hosting actor must
# implement the pending-approval continuation (persist pending state, escalate,
# resume on decision, time out). Wiring YieldApprovalHandler into a surface that
# lacks that continuation produces a dead-letter approval: the tool result tells
# the LLM an approval is pending, nobody can ever approve it, and the turn
# completes as if nothing is missing.
#
# Rule 1: no container-level IToolApprovalHandler registration in production code.
#         A DI-global handler hands "I will resume you" to every surface,
#         including ones with no continuation (the #2004 dead letter).
# Rule 2: `new YieldApprovalHandler()` may appear only in production files that
#         own the pending-approval continuation. RoleGAgent is the only such
#         owner today; it wires the handler as its capability default. Adding a
#         file to this allowlist requires that the type (or its base chain)
#         consumes ToolCallContext.PendingApproval and persists it.
#
# Scan: src/**/*.cs and agents/**/*.cs, excluding bin/obj and test projects.

violations=""

di_registration_hits=$(grep -rnE 'Add(Singleton|Scoped|Transient)[^;]*IToolApprovalHandler|TryAdd(Singleton|Scoped|Transient)?[^;]*IToolApprovalHandler' \
  --include='*.cs' src agents 2>/dev/null \
  | grep -vE '/(bin|obj)/' \
  | grep -vE '(Tests?|Test)/' || true)

if [[ -n "${di_registration_hits}" ]]; then
  violations+="Container-level IToolApprovalHandler registration is forbidden (yield capability must follow the actor, not the container):\n${di_registration_hits}\n"
fi

yield_allowlist=(
  'src/Aevatar.AI.Core/RoleGAgent.cs'
)

yield_hits=$(grep -rln 'new YieldApprovalHandler(' \
  --include='*.cs' src agents 2>/dev/null \
  | grep -vE '/(bin|obj)/' \
  | grep -vE '(Tests?|Test)/' || true)

while IFS= read -r file; do
  [[ -z "${file}" ]] && continue
  allowed="false"
  for entry in "${yield_allowlist[@]}"; do
    if [[ "${file}" == "${entry}" ]]; then
      allowed="true"
      break
    fi
  done
  if [[ "${allowed}" == "false" ]]; then
    violations+="YieldApprovalHandler wired outside the pending-approval continuation allowlist: ${file}\n"
  fi
done <<< "${yield_hits}"

if [[ -n "${violations}" ]]; then
  echo "Tool approval wiring guard FAILED:"
  echo -e "${violations}"
  exit 1
fi

echo "Tool approval wiring guard passed."
