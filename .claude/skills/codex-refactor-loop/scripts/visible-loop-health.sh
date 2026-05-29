#!/usr/bin/env bash
# Read-only fallback health report for visible loop workers.
#
# This helper is intentionally smaller than peek.sh. It does not require
# .refactor-loop/host.env, does not call GitHub, and does not mutate loop state.
# Use it when the preferred peek path is unavailable or cannot be trusted.

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script_git_root="$(git -C "$script_dir" rev-parse --show-toplevel 2>/dev/null || true)"
pwd_git_root="$(git rev-parse --show-toplevel 2>/dev/null || true)"

repo_root="${REPO_ROOT:-${script_git_root:-$pwd_git_root}}"
host_env="$repo_root/.refactor-loop/host.env"
host_env_status="missing"

if [[ -f "$host_env" ]]; then
  host_env_status="present"
  set +e +u
  # shellcheck disable=SC1090
  source "$host_env"
  source_rc=$?
  set -euo pipefail
  if [[ "$source_rc" -eq 0 ]]; then
    host_env_status="loaded"
    repo_root="${REPO_ROOT:-$repo_root}"
  else
    host_env_status="load-failed"
  fi
fi

if [[ -z "$repo_root" ]]; then
  echo "visible_loop_process_health"
  echo "status=blocked"
  echo "reason=not inside a git repository and REPO_ROOT is unset"
  exit 2
fi

refactor_dir="$repo_root/.refactor-loop"
log_dir="$refactor_dir/logs"
prompt_dir="$refactor_dir/prompts"
run_dir="$refactor_dir/runs"
peek_script="$repo_root/.claude/skills/codex-refactor-loop/scripts/peek.sh"
log_summary_script="$repo_root/.claude/skills/codex-refactor-loop/scripts/visible-loop-log-summary.sh"

count_files() {
  local dir="$1"
  if [[ ! -d "$dir" ]]; then
    echo 0
    return
  fi
  find "$dir" -type f 2>/dev/null | wc -l | tr -d ' '
}

tail_has_exit() {
  local file="$1"
  tail -5 "$file" 2>/dev/null | grep -q "^EXIT="
}

print_recent_markers() {
  if [[ ! -d "$log_dir" ]]; then
    echo "  none: logs directory missing"
    return
  fi

  local found=0
  while IFS= read -r file; do
    [[ -f "$file" ]] || continue
    tail_has_exit "$file" || continue
    marker="$(
      grep -hE "^[+]?(AUDIT_DONE|AUDIT_INCOMPLETE|IMPLEMENT_DONE|IMPLEMENT_BLOCKED|FIX_DONE|FIX_BLOCKED|REVIEW_DONE|SOLVER_DONE|META_JUDGE_DONE|META_RESOLVED|TEST_ADD_DONE):" "$file" 2>/dev/null \
        | sed -E 's/^[+]+//' \
        | tail -1 \
        | head -c 160 \
        || true
    )"
    [[ -n "$marker" ]] || continue
    found=1
    echo "  $(basename "$file"): $marker"
  done < <(find "$log_dir" -name "*.log" -type f -mmin -60 2>/dev/null | sort)

  if [[ "$found" -eq 0 ]]; then
    echo "  none"
  fi
}

print_active_logs() {
  if [[ ! -d "$log_dir" ]]; then
    echo "  none: logs directory missing"
    return
  fi

  local found=0
  while IFS= read -r file; do
    [[ -f "$file" ]] || continue
    tail_has_exit "$file" && continue
    found=1
    echo "  $(basename "$file")"
  done < <(find "$log_dir" -name "*.log" -type f -mmin -10 2>/dev/null | sort)

  if [[ "$found" -eq 0 ]]; then
    echo "  none"
  fi
}

print_visible_loop_log_summaries() {
  if [[ ! -d "$log_dir" ]]; then
    echo "  none: logs directory missing"
    return
  fi

  if [[ ! -x "$log_summary_script" ]]; then
    echo "  none: log summary helper missing path=$log_summary_script"
    return
  fi

  local files=()
  local file
  while IFS= read -r file; do
    files+=("$file")
  done < <(find "$log_dir" -name "visible-*.log" -type f -mmin -60 2>/dev/null | sort || true)

  if [[ "${#files[@]}" -eq 0 ]]; then
    echo "  none"
    return
  fi

  "$log_summary_script" "${files[@]}" | sed 's/^/  /'
}

process_report() {
  local repo_scoped=0
  local loop_like=0
  local direct_codex=0
  local scoped_lines=()
  local loop_like_lines=()

  while IFS= read -r line; do
    if [[ "$line" == *"codex exec"* ]]; then
      direct_codex=$((direct_codex + 1))
    fi
    if [[ "$line" == *"spawn-codex.sh"* ]] &&
       { [[ "$line" == *".refactor-loop/logs/"* ]] || [[ "$line" == *".refactor-loop/prompts/"* ]]; }; then
      loop_like=$((loop_like + 1))
      if [[ "$line" == *"$repo_root"* ]]; then
        repo_scoped=$((repo_scoped + 1))
        scoped_lines+=("$line")
      else
        loop_like_lines+=("$line")
      fi
    fi
  done < <(ps -eo command= 2>/dev/null || ps -ef 2>/dev/null || true)

  echo "process_counts:"
  echo "  repo_scoped_spawn_codex=$repo_scoped"
  echo "  loop_like_spawn_codex=$loop_like"
  echo "  direct_codex_exec=$direct_codex"

  if [[ "${#scoped_lines[@]}" -gt 0 ]]; then
    echo "repo_scoped_processes:"
    printf '  %s\n' "${scoped_lines[@]}" | sed -E 's/[[:space:]]+/ /g' | head -5
  fi

  if [[ "$repo_scoped" -eq 0 && "${#loop_like_lines[@]}" -gt 0 ]]; then
    echo "unscoped_loop_like_processes:"
    printf '  %s\n' "${loop_like_lines[@]}" | sed -E 's/[[:space:]]+/ /g' | head -5
  fi
}

path_status() {
  local path="$1"
  if [[ -d "$path" ]]; then
    echo "present"
  else
    echo "missing"
  fi
}

file_status() {
  local path="$1"
  if [[ -f "$path" ]]; then
    echo "present"
  else
    echo "missing"
  fi
}

echo "visible_loop_process_health"
echo "repo_root=$repo_root"
echo "host_env=$host_env_status path=$host_env"
echo "peek_sh=$(file_status "$peek_script") path=$peek_script"
echo "log_summary_sh=$(file_status "$log_summary_script") path=$log_summary_script"
echo "refactor_loop_dir=$(path_status "$refactor_dir")"
echo "logs_dir=$(path_status "$log_dir") file_count=$(count_files "$log_dir")"
echo "prompts_dir=$(path_status "$prompt_dir") file_count=$(count_files "$prompt_dir")"
echo "runs_dir=$(path_status "$run_dir") file_count=$(count_files "$run_dir")"
process_report
echo "active_logs_last_10m:"
print_active_logs
echo "recent_finished_markers_last_60m:"
print_recent_markers
echo "visible_loop_log_summaries_last_60m:"
print_visible_loop_log_summaries
echo "monitor_files:"
echo "  alert_log=$(file_status "$refactor_dir/.concurrency-alert.log")"
echo "  pending_events=$(file_status "$refactor_dir/.controller-pending-events.log")"
echo "  monitor_state=$(file_status "$refactor_dir/.concurrency-monitor-state.json")"

if [[ "$host_env_status" == "missing" || ! -f "$peek_script" || ! -d "$log_dir" ]]; then
  echo "conservative_status=degraded"
else
  echo "conservative_status=available"
fi
