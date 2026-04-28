#!/usr/bin/env bash
#
# Issue #466 §D: secret-bearing IUserAgentDeliveryTargetReader is reserved for
# outbound delivery components (FeishuCardHumanInteractionPort + analogous
# Telegram outbound). NO IAgentTool implementation may reach the secret-bearing
# reader — that would re-create the credential-exfiltration surface this
# refactor closed. The guard scans every IAgentTool concrete class and checks
# its source file for either:
#
#   1. A constructor parameter of type IUserAgentDeliveryTargetReader, OR
#   2. An IServiceProvider.GetService<IUserAgentDeliveryTargetReader>() call.
#
# Both patterns are equivalent secret-boundary violations: the (2) escape hatch
# matters because IAgentTool implementations historically take IServiceProvider
# directly, so a constructor-only check would let a `sp.GetService<...>()`
# slip through. Doc-comment mentions (`<see cref="IUserAgentDeliveryTargetReader"/>`)
# and string literals are explicitly tolerated.
#
# Failure mode: a future PR injects the secret-bearing reader into a tool class
# via constructor or service-provider lookup. CI fails fast with offending file.

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

# Pattern (1) — constructor-parameter dependency:
#   `IUserAgentDeliveryTargetReader <name>` followed by `,` or `)`.
# Excludes doc-comment / string-literal mentions because those don't have a
# parameter name + delimiter immediately after the type.
ctor_param_pattern='\bIUserAgentDeliveryTargetReader\s+\w+\s*[,)]'

# Pattern (2) — IServiceProvider escape hatch:
#   `GetService<IUserAgentDeliveryTargetReader>()`
#   `GetRequiredService<IUserAgentDeliveryTargetReader>()`
# Tools that take IServiceProvider directly (AgentBuilderTool / AgentDeliveryTargetTool
# in this repo) bypass the constructor-parameter check, so this catches them.
sp_lookup_pattern='\bGet(?:Required)?Service<\s*IUserAgentDeliveryTargetReader\s*>'

violators=""
while IFS= read -r file; do
  [ -z "${file}" ] && continue
  if rg -q --pcre2 "${ctor_param_pattern}" "${file}" 2>/dev/null \
     || rg -q --pcre2 "${sp_lookup_pattern}" "${file}" 2>/dev/null; then
    violators+="${file}"$'\n'
  fi
done <<< "${agent_tool_files}"

if [ -n "${violators}" ]; then
  echo "agent_tool_delivery_target_reader_guard: IAgentTool implementation reaches IUserAgentDeliveryTargetReader (issue #466 §D — secret boundary):"
  echo "${violators}"
  echo
  echo "LLM-facing tools (IAgentTool) must read agent ownership through IUserAgentCatalogQueryPort"
  echo "(caller-scoped, no NyxApiKey in the public DTO). The secret-bearing reader is reserved for"
  echo "outbound delivery components like FeishuCardHumanInteractionPort. Both constructor-parameter"
  echo "injection AND IServiceProvider.GetService<IUserAgentDeliveryTargetReader>() are forbidden."
  exit 1
fi

echo "agent_tool_delivery_target_reader_guard: ok"
