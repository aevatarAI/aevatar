#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Refactor (iter42/issue-864-studio-workspace-execution-fact-owner):
#   Old pattern: Studio executions/workspace facts mixed FileStudioWorkspaceStore JSON, draft index sidecars, and authoritative server UI/layout state across multiple owners.
#   New principle: Studio executions are a bounded ServiceRunGAgent readmodel facade; UI/layout/draft index are deleted/downgraded to client cache or derived from existing actor-backed sources. No new history/draft index actor.

scan_roots=()
for root in src tools; do
  if [ -e "${root}" ]; then
    scan_roots+=("${root}")
  fi
done

if [ "${#scan_roots[@]}" -eq 0 ]; then
  exit 0
fi

grep_source_files() {
  local pattern="$1"
  shift

  find "${scan_roots[@]}" \
    -path '*/bin/*' -prune -o \
    -path '*/obj/*' -prune -o \
    -path '*/node_modules/*' -prune -o \
    -path '*/wwwroot/*' -prune -o \
    -type f \( "$@" \) -print0 \
    | xargs -0 grep -nEH "${pattern}" 2>/dev/null || true
}

forbidden_file_hits="$(
  find "${scan_roots[@]}" \
    -path '*/bin/*' -prune -o \
    -path '*/obj/*' -prune -o \
    -path '*/node_modules/*' -prune -o \
    -path '*/wwwroot/*' -prune -o \
    -type f \( \
      -path '*/executions/*.json' -o \
      -name 'workflow-draft-index.json' -o \
      -name '*layout*.json' -o \
      -path '*scope-workflow-layouts*' \
    \) -print || true
)"

if [ -n "${forbidden_file_hits}" ]; then
  echo "${forbidden_file_hits}"
  echo "Studio production JSON fact files are forbidden. Derive drafts from actor-backed readmodels and keep layout as client cache/import-export artifact."
  exit 1
fi

set +e
forbidden_symbol_pattern="FileStudioWorkspaceStore|IStudioWorkspaceStore|StoredExecutionRecord|StudioExecutionHistory|ScopeExecutionHistory|DraftIndexActor|IDraftIndexStore|workflow-draft-index\.json|executions/[^\"]*\.json"
if command -v rg >/dev/null 2>&1; then
  forbidden_symbol_hits="$(
    rg -n "${forbidden_symbol_pattern}" \
    "${scan_roots[@]}" \
    -g '*.cs' \
    -g '*.proto' \
    -g '*.json' \
    -g '*.sh' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    -g '!**/node_modules/**' \
    -g '!**/wwwroot/**' \
    -g '!tools/ci/studio_fact_owner_guard.sh' \
      | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);
  trimmed = text;
  sub(/^[[:space:]]+/, "", trimmed);

  if (file ~ /(^|\/)test\// || file ~ /Tests\.cs$/ || file ~ /(^|\/)[^\/]*\.Tests\//)
    next;
  if (trimmed ~ /^(\/\/|#|\*)/)
    next;
  if (trimmed ~ /^<!--/)
    next;

  print $0;
}'
  )"
  forbidden_symbol_status=$?
else
  forbidden_symbol_hits="$(
    grep_source_files "${forbidden_symbol_pattern}" \
      -name '*.cs' -o \
      -name '*.proto' -o \
      -name '*.json' -o \
      -name '*.sh' \
      | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);
  trimmed = text;
  sub(/^[[:space:]]+/, "", trimmed);

  if (file ~ /(^|\/)test\// || file ~ /Tests\.cs$/ || file ~ /(^|\/)[^\/]*\.Tests\//)
    next;
  if (file == "tools/ci/studio_fact_owner_guard.sh")
    next;
  if (trimmed ~ /^(\/\/|#|\*)/)
    next;
  if (trimmed ~ /^<!--/)
    next;

  print $0;
}'
  )"
  forbidden_symbol_status=$?
fi
set -e

if [[ ${forbidden_symbol_status} -ne 0 && ${forbidden_symbol_status} -ne 1 ]]; then
  echo "Studio fact-owner guard execution failed."
  exit "${forbidden_symbol_status}"
fi

if [ -n "${forbidden_symbol_hits}" ]; then
  echo "${forbidden_symbol_hits}"
  echo "Studio execution/workspace fact owner regression found. Do not resurrect local execution history, draft index sidecars, or FileStudioWorkspaceStore production paths."
  exit 1
fi

set +e
forbidden_ui_layout_pattern="WorkflowLayoutDocument|request\.(Layout|AppearanceTheme|ColorMode)|(^|[^[:alnum:]_])Layout:[[:space:]]*([A-Za-z_][A-Za-z0-9_]*|.*\.(Layout|AppearanceTheme|ColorMode))|HasLayout:[[:space:]]*true|HasLayout[[:space:]]*=[[:space:]]*true"
if command -v rg >/dev/null 2>&1; then
  forbidden_ui_layout_hits="$(
    rg -n "${forbidden_ui_layout_pattern}" \
    "${scan_roots[@]}" \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    -g '!**/node_modules/**' \
    -g '!**/wwwroot/**' \
    -g '!tools/ci/studio_fact_owner_guard.sh' \
      | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);
  trimmed = text;
  sub(/^[[:space:]]+/, "", trimmed);

  if (file ~ /(^|\/)test\// || file ~ /Tests\.cs$/ || file ~ /(^|\/)[^\/]*\.Tests\//)
    next;
  if (file ~ /^src\/Aevatar\.Studio\.Application\/Studio\/Contracts\//)
    next;
  if (file ~ /^src\/Aevatar\.Studio\.Application\/Studio\/Abstractions\//)
    next;
  if (file == "src/Aevatar.Studio.Domain/Studio/Models/WorkflowLayoutDocument.cs")
    next;
  if (trimmed ~ /^(\/\/|#|\*)/)
    next;
  if (trimmed ~ /^<!--/)
    next;
  if (trimmed ~ /^Layout:[[:space:]]*null[,\);]*$/)
    next;
  if (trimmed ~ /^HasLayout:[[:space:]]*false[,\)]?$/)
    next;

  print $0;
}'
  )"
  forbidden_ui_layout_status=$?
else
  forbidden_ui_layout_hits="$(
    grep_source_files "${forbidden_ui_layout_pattern}" \
      -name '*.cs' \
      | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);
  trimmed = text;
  sub(/^[[:space:]]+/, "", trimmed);

  if (file ~ /(^|\/)test\// || file ~ /Tests\.cs$/ || file ~ /(^|\/)[^\/]*\.Tests\//)
    next;
  if (file ~ /^src\/Aevatar\.Studio\.Application\/Studio\/Contracts\//)
    next;
  if (file ~ /^src\/Aevatar\.Studio\.Application\/Studio\/Abstractions\//)
    next;
  if (file == "src/Aevatar.Studio.Domain/Studio/Models/WorkflowLayoutDocument.cs")
    next;
  if (file == "tools/ci/studio_fact_owner_guard.sh")
    next;
  if (trimmed ~ /^(\/\/|#|\*)/)
    next;
  if (trimmed ~ /^<!--/)
    next;
  if (trimmed ~ /^Layout:[[:space:]]*null[,\);]*$/)
    next;
  if (trimmed ~ /^HasLayout:[[:space:]]*false[,\)]?$/)
    next;

  print $0;
}'
  )"
  forbidden_ui_layout_status=$?
fi
set -e

if [[ ${forbidden_ui_layout_status} -ne 0 && ${forbidden_ui_layout_status} -ne 1 ]]; then
  echo "Studio UI/layout fact guard execution failed."
  exit "${forbidden_ui_layout_status}"
fi

if [ -n "${forbidden_ui_layout_hits}" ]; then
  echo "${forbidden_ui_layout_hits}"
  echo "Studio UI/layout facts are client-owned compatibility fields. Production paths must not map request layout/theme/color into actor events, storage, readmodels, or query truth."
  exit 1
fi
