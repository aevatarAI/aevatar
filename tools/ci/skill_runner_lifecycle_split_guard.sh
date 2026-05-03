#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Issue #491 guard: enforce SkillRunner → SkillDefinition + SkillExecution lifecycle split.
#
# 1. No actor class named SkillRunnerGAgent (must be renamed to SkillDefinitionGAgent).
# 2. SkillDefinitionGAgent state proto contains zero per-execution fields
#    (last_run_at, error_count, last_output, last_error, retry_attempt).
# 3. No per-template actor types (DailyReport*GAgent, SocialMedia*GAgent, etc.).

violations=""

# ── Rule 1: forbid SkillRunnerGAgent class declaration ──
while IFS= read -r hit; do
  [ -z "${hit}" ] && continue
  violations="${violations}${hit}
"
done < <(
  rg -n '(class|record|struct|interface)[[:space:]]+SkillRunnerGAgent\b' \
    --glob '*.cs' \
    --glob '!**/bin/**' \
    --glob '!**/obj/**' \
    --glob '!docs/**' \
    --glob '!tools/ci/skill_runner_lifecycle_split_guard.sh' \
    || true
)

# ── Rule 2: SkillDefinitionState proto must not contain per-execution fields ──
definition_proto="agents/Aevatar.GAgents.Scheduled/protos/skill_definition.proto"
if [ -f "${definition_proto}" ]; then
  forbidden_fields=(
    'last_run_at'
    'error_count'
    'last_output'
    'last_error'
    'retry_attempt'
  )
  for field in "${forbidden_fields[@]}"; do
    while IFS= read -r hit; do
      [ -z "${hit}" ] && continue
      violations="${violations}${definition_proto}:${hit}  [per-execution field '${field}' in SkillDefinitionState]
"
    done < <(
      # Only match actual proto field declarations (not comments or reserved)
      # inside the SkillDefinitionState message block.
      awk '/^message SkillDefinitionState/,/^}/' "${definition_proto}" \
        | grep -v '^\s*//' \
        | grep -v '^\s*reserved' \
        | grep -E "^\s*(repeated\s+|optional\s+|map<)?\w+\s+${field}\s*=" || true
    )
  done
else
  violations="${violations}${definition_proto}: MISSING — SkillDefinitionState proto not found
"
fi

# ── Rule 3: forbid per-template actor types ──
per_template_patterns=(
  'DailyReport[A-Za-z]*GAgent'
  'SocialMedia[A-Za-z]*GAgent'
  'SocialMediaPost[A-Za-z]*GAgent'
)

for pattern in "${per_template_patterns[@]}"; do
  while IFS= read -r hit; do
    [ -z "${hit}" ] && continue
    violations="${violations}${hit}  [per-template actor type forbidden — use SkillDefinitionGAgent with instance config]
"
  done < <(
    rg -n "(class|record|struct|interface)[[:space:]]+${pattern}" \
      --glob '*.cs' \
      --glob '!**/bin/**' \
      --glob '!**/obj/**' \
      --glob '!docs/**' \
      --glob '!tools/ci/skill_runner_lifecycle_split_guard.sh' \
      || true
  )
done

if [ -n "${violations}" ]; then
  printf '%s' "${violations}"
  echo "skill_runner_lifecycle_split_guard: FAILED — see violations above (issue #491)."
  exit 1
fi

echo "skill_runner_lifecycle_split_guard: ok"
