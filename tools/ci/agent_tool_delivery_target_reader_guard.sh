#!/usr/bin/env bash
#
# Issue #466 §D: secret-bearing IUserAgentDeliveryTargetReader is reserved for
# outbound delivery components (FeishuCardHumanInteractionPort + analogous
# Telegram outbound). NO IAgentTool implementation may take it as a constructor
# dependency — that would re-create the credential-exfiltration surface this
# refactor closed. The guard scans every IAgentTool concrete class and checks
# its source file for a *constructor parameter* of the secret-bearing reader
# type. Doc comments / DI-registration comments mentioning the type for context
# are explicitly tolerated.
#
# Failure mode: a future PR injects the secret-bearing reader into a tool class
# via constructor dependency. CI fails fast with the offending file path.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Match class-declaration syntax precisely: `class Name : ...IAgentTool[,)]` so
# IAgentToolSource (the registration interface) doesn't trip the regex.
agent_tool_files="$(
  rg -l --type cs --pcre2 '\bclass\s+\w+\s*:\s*[^{}]*\bIAgentTool\b(?!\w)' agents src 2>/dev/null || true
)"

if [ -z "${agent_tool_files}" ]; then
  echo "agent_tool_delivery_target_reader_guard: no IAgentTool implementations found (skipping)"
  exit 0
fi

# Pattern for an actual constructor-parameter dependency:
#   `IUserAgentDeliveryTargetReader <name>` (with a non-space, non-comma after)
# This excludes doc-comment mentions (`<see cref="IUserAgentDeliveryTargetReader"/>`)
# and string-literal references.
violators=""
while IFS= read -r file; do
  [ -z "${file}" ] && continue
  if rg -q --pcre2 '\bIUserAgentDeliveryTargetReader\s+\w+\s*[,)]' "${file}" 2>/dev/null; then
    violators+="${file}"$'\n'
  fi
done <<< "${agent_tool_files}"

if [ -n "${violators}" ]; then
  echo "agent_tool_delivery_target_reader_guard: IAgentTool implementation depends on IUserAgentDeliveryTargetReader (issue #466 §D — secret boundary):"
  echo "${violators}"
  echo
  echo "LLM-facing tools (IAgentTool) must read agent ownership through IUserAgentCatalogQueryPort"
  echo "(caller-scoped, no NyxApiKey in the public DTO). The secret-bearing reader is reserved for"
  echo "outbound delivery components like FeishuCardHumanInteractionPort."
  exit 1
fi

echo "agent_tool_delivery_target_reader_guard: ok"
