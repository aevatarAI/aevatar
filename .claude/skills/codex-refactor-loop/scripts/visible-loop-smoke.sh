#!/usr/bin/env bash
# Read-only local smoke runner for visible-loop process helpers.
#
# This script is intentionally local-only. It does not call GitHub, does not
# write loop state, and does not modify tracked files. Pass an optional log
# directory to include visible worker log summaries. Pass an optional second
# argument to validate a local PR body draft.

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(git -C "$script_dir" rev-parse --show-toplevel 2>/dev/null || git rev-parse --show-toplevel 2>/dev/null || true)"
log_dir_arg="${1:-}"
pr_body_arg="${2:-}"
status=0

if [[ -z "$repo_root" ]]; then
  echo "visible_loop_smoke"
  echo "status=blocked"
  echo "reason=not inside a git repository"
  exit 2
fi

portable_timeout="$script_dir/portable-timeout.sh"
log_summary="$script_dir/visible-loop-log-summary.sh"
peek="$script_dir/peek.sh"
health="$script_dir/visible-loop-health.sh"
prompt_guard="$script_dir/visible-loop-prompt-guard.sh"
batch_files="$script_dir/visible-loop-batch-files.sh"
pr_body_check="$script_dir/visible-loop-pr-body-check.sh"

run_check() {
  local name="$1"
  shift

  echo "check=$name"
  set +e
  "$@"
  local rc=$?
  set -e

  if [[ "$rc" -eq 0 ]]; then
    echo "result=$name ok"
  else
    echo "result=$name failed exit=$rc"
    status=1
  fi
}

discover_log_dir() {
  if [[ -n "$log_dir_arg" ]]; then
    printf '%s\n' "$log_dir_arg"
    return
  fi

  if [[ -d "$repo_root/.refactor-loop/logs" ]]; then
    printf '%s\n' "$repo_root/.refactor-loop/logs"
    return
  fi

  if [[ -d "/Users/abigaildeng/Documents/Playground/aevatar/.refactor-loop/logs" ]]; then
    printf '%s\n' "/Users/abigaildeng/Documents/Playground/aevatar/.refactor-loop/logs"
    return
  fi
}

discover_prompt_files() {
  local dir
  local file

  for dir in \
    "$repo_root/.refactor-loop/prompts" \
    "$(dirname "${log_dir_arg:-/none}")/prompts" \
    "/Users/abigaildeng/Documents/Playground/aevatar/.refactor-loop/prompts"
  do
    [[ -d "$dir" ]] || continue
    found_any=0
    while IFS= read -r file; do
      found_any=1
      printf '%s\n' "$file"
    done < <(find "$dir" -maxdepth 1 -type f -name "visible-*.md" | sort)
    [[ "$found_any" -eq 0 ]] || return
  done
}

print_capped_helper_output() {
  local name="$1"
  local helper="$2"
  local max_lines="$3"

  echo "check=${name}_capped"
  if [[ ! -f "$helper" ]]; then
    echo "result=${name}_capped skipped reason=missing path=$helper"
    return
  fi

  local output
  set +e
  output="$(bash "$helper" 2>&1)"
  local rc=$?
  set -e

  printf '%s\n' "$output" | sed -n "1,${max_lines}p"

  if [[ "$rc" -eq 0 ]]; then
    echo "result=${name}_capped ok"
  else
    echo "result=${name}_capped degraded exit=$rc"
  fi
}

echo "visible_loop_smoke"
echo "repo_root=$repo_root"
echo "scripts_dir=$script_dir"
echo "github_state=skipped local_read_only_smoke"

run_check "git_diff_check" git -C "$repo_root" diff --check

echo "check=bash_syntax"
while IFS= read -r script_path; do
  [[ -f "$script_path" ]] || continue
  run_check "bash_n_$(basename "$script_path")" bash -n "$script_path"
done < <(find "$script_dir" -maxdepth 1 -type f -name "*.sh" | sort)
echo "result=bash_syntax complete"

if [[ -x "$portable_timeout" ]]; then
  run_check "portable_timeout_select" "$portable_timeout" --select
  run_check "portable_timeout_forced_fallback" env CODEX_LOOP_FORCE_TIMEOUT_FALLBACK=1 "$portable_timeout" 2 bash -c 'exit 0'
else
  echo "result=portable_timeout failed reason=missing_or_not_executable path=$portable_timeout"
  status=1
fi

echo "check=visible_loop_prompt_guard"
prompt_files=()
while IFS= read -r prompt_file; do
  [[ -n "$prompt_file" ]] || continue
  prompt_files+=("$prompt_file")
done < <(discover_prompt_files || true)
if [[ "${#prompt_files[@]}" -gt 0 && -x "$prompt_guard" ]]; then
  run_check "visible_loop_prompt_guard" "$prompt_guard" "${prompt_files[@]}"
elif [[ "${#prompt_files[@]}" -eq 0 ]]; then
  echo "result=visible_loop_prompt_guard skipped reason=no_visible_prompts_discovered"
else
  echo "result=visible_loop_prompt_guard failed reason=missing_or_not_executable path=$prompt_guard"
  status=1
fi

echo "check=visible_loop_batch_files"
if [[ -x "$batch_files" ]]; then
  set +e
  "$batch_files" 10
  batch_rc=$?
  set -e
  if [[ "$batch_rc" -eq 0 ]]; then
    echo "result=visible_loop_batch_files readiness=reached"
  else
    echo "result=visible_loop_batch_files readiness=below_threshold exit=$batch_rc"
  fi
else
  echo "result=visible_loop_batch_files failed reason=missing_or_not_executable path=$batch_files"
  status=1
fi

if [[ -n "$pr_body_arg" ]]; then
  if [[ -x "$pr_body_check" ]]; then
    run_check "visible_loop_pr_body_check" "$pr_body_check" "$pr_body_arg"
  else
    echo "result=visible_loop_pr_body_check failed reason=missing_or_not_executable path=$pr_body_check"
    status=1
  fi
else
  echo "result=visible_loop_pr_body_check skipped reason=no_pr_body_path_supplied"
fi

log_dir="$(discover_log_dir || true)"
echo "log_dir=${log_dir:-none}"
if [[ -n "${log_dir:-}" && -d "$log_dir" && -x "$log_summary" ]]; then
  visible_logs=()
  while IFS= read -r log_path; do
    visible_logs+=("$log_path")
  done < <(find "$log_dir" -maxdepth 1 -type f -name "visible-*.log" | sort | tail -20)
  if [[ "${#visible_logs[@]}" -gt 0 ]]; then
    echo "check=visible_loop_log_summary"
    "$log_summary" "${visible_logs[@]}" | sed -n '1,40p'
    echo "result=visible_loop_log_summary ok files=${#visible_logs[@]}"
  else
    echo "result=visible_loop_log_summary skipped reason=no_visible_logs"
  fi
else
  echo "result=visible_loop_log_summary skipped reason=missing_log_dir_or_helper"
fi

print_capped_helper_output "peek" "$peek" 80
print_capped_helper_output "visible_loop_health" "$health" 80

if [[ "$status" -eq 0 ]]; then
  echo "visible_loop_smoke_result=ok"
else
  echo "visible_loop_smoke_result=failed"
fi

exit "$status"
