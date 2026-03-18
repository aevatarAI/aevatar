#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

if ! rg -n "SubWorkflowInvokeRequestedEvent" test/Aevatar.Integration.Tests/WorkflowCoreModulesCoverageTests.cs >/dev/null; then
  echo "Missing workflow_call request-path coverage assertion in WorkflowCoreModulesCoverageTests."
  exit 1
fi

if ! rg -n "workflow_call.child_run_id" test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs >/dev/null; then
  echo "Missing workflow_call return-path coverage assertion in WorkflowGAgentCoverageTests."
  exit 1
fi

if ! rg -n "WorkflowTuringCompletenessTests" test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs >/dev/null; then
  echo "Missing closed-world turing-completeness integration proof tests."
  exit 1
fi

echo "Closed-world workflow guards passed."
