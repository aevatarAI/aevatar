#!/usr/bin/env bash
# Read-only PR body readiness guard for visible-loop automation batches.
#
# This script validates a local PR body draft only. It does not call GitHub and
# does not create, edit, or publish a PR.

set -euo pipefail

usage() {
  echo "usage: visible-loop-pr-body-check.sh <pr-body-file>" >&2
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

if [[ $# -ne 1 ]]; then
  usage
  exit 2
fi

body_file="$1"
status=0
required_base="feat/2026-05-29_ai-automation-pipeline"

echo "visible_loop_pr_body_check"
echo "body_file=$body_file"

if [[ ! -f "$body_file" ]]; then
  echo "status=blocked"
  echo "reason=body_file_not_found"
  exit 2
fi

check_pattern() {
  local name="$1"
  local pattern="$2"

  if grep -Eiq "$pattern" "$body_file"; then
    echo "check=$name result=ok"
  else
    echo "check=$name result=failed"
    status=1
  fi
}

check_problem_solution() {
  local has_problem=0
  local has_solution=0

  grep -Eiq '(problem|issue|why)' "$body_file" && has_problem=1
  grep -Eiq '(solution|approach|fix|change)' "$body_file" && has_solution=1

  if [[ "$has_problem" -eq 1 && "$has_solution" -eq 1 ]]; then
    echo "check=problem_solution result=ok"
  else
    echo "check=problem_solution result=failed"
    status=1
  fi
}

check_validation() {
  local has_validation=0
  local has_result=0

  grep -Eiq '(validation|verification|validated|verify)' "$body_file" && has_validation=1
  grep -Eiq '(command|result|passed|ok|exit=0)' "$body_file" && has_result=1

  if [[ "$has_validation" -eq 1 && "$has_result" -eq 1 ]]; then
    echo "check=validation_commands_results result=ok"
  else
    echo "check=validation_commands_results result=failed"
    status=1
  fi
}

check_file_list() {
  local path_count
  local has_list_phrase=0

  path_count="$(
    grep -Eo '\.claude/skills/codex-refactor-loop/[^ )`",]+' "$body_file" 2>/dev/null \
      | sort -u \
      | wc -l \
      | tr -d ' '
  )"
  grep -Eiq '(10[- ]file|ten[- ]file|file list|meaningful file)' "$body_file" && has_list_phrase=1

  echo "check=ten_file_list path_count=$path_count"
  if [[ "$has_list_phrase" -eq 1 && "$path_count" -ge 10 ]]; then
    echo "check=ten_file_list result=ok"
  else
    echo "check=ten_file_list result=failed"
    status=1
  fi
}

check_required_base() {
  if grep -Fq "$required_base" "$body_file"; then
    echo "check=required_base result=ok base=$required_base"
  else
    echo "check=required_base result=failed base=$required_base"
    status=1
  fi
}

check_problem_solution
check_pattern "same_batch_rationale" '(same[- ]batch|same batch|batch rationale|rationale.*batch|why.*batch)'
check_pattern "affected_paths" '(affected paths?|changed paths?|impact paths?|paths touched|path impact)'
check_file_list
check_validation
check_pattern "browser_runtime_evidence" '((browser|runtime).*(evidence|not applicable|not-applicable|n/a|not needed)|(evidence|not applicable|not-applicable|n/a|not needed).*(browser|runtime))'
check_pattern "dirty_workspace_protection" '(dirty[- ]workspace|dirty workspace|dirty worktree|user dirty|shared dirty|frontend dirty|protect.*dirty|dirty.*protect)'
check_required_base

if [[ "$status" -eq 0 ]]; then
  echo "visible_loop_pr_body_check_result=ok"
else
  echo "visible_loop_pr_body_check_result=failed"
fi

exit "$status"
