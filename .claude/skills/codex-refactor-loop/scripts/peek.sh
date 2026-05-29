#!/usr/bin/env bash
# Stable read-only controller visibility entrypoint for codex-refactor-loop.
#
# This script intentionally prefers a local process/log snapshot. It does not
# fetch, push, post comments, change labels, or call GitHub. When key loop
# directories are missing, it delegates to visible-loop-health.sh as a degraded
# helper so controller wakeups still have one stable command to run.

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
helper_script="$script_dir/visible-loop-health.sh"
log_summary_script="$script_dir/visible-loop-log-summary.sh"
script_git_root="$(git -C "$script_dir" rev-parse --show-toplevel 2>/dev/null || true)"
pwd_git_root="$(git rev-parse --show-toplevel 2>/dev/null || true)"

repo_root="${REPO_ROOT:-${script_git_root:-$pwd_git_root}}"
host_env_status="missing"

if [[ -n "$repo_root" && -f "$repo_root/.refactor-loop/host.env" ]]; then
  host_env_status="present"
  set +e +u
  # shellcheck disable=SC1090
  source "$repo_root/.refactor-loop/host.env"
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
  echo "codex_refactor_loop_peek"
  echo "status=degraded"
  echo "reason=not inside a git repository and REPO_ROOT is unset"
  if [[ -x "$helper_script" ]]; then
    echo "delegated_helper=$helper_script"
    bash "$helper_script" || true
  else
    echo "delegated_helper=missing path=$helper_script"
  fi
  exit 0
fi

refactor_dir="$repo_root/.refactor-loop"
log_dir="$refactor_dir/logs"
prompt_dir="$refactor_dir/prompts"
run_dir="$refactor_dir/runs"

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

count_files() {
  local dir="$1"
  if [[ ! -d "$dir" ]]; then
    echo 0
    return
  fi
  find "$dir" -type f 2>/dev/null | wc -l | tr -d ' '
}

tail_exit_line() {
  local file="$1"
  tail -5 "$file" 2>/dev/null | grep -E "^EXIT=[0-9]+$" | tail -1 || true
}

tail_exit_code() {
  local file="$1"
  local line
  line="$(tail_exit_line "$file")"
  if [[ -z "$line" ]]; then
    echo "none"
  else
    echo "${line#EXIT=}"
  fi
}

marker_from_log() {
  local file="$1"
  grep -hE "^[+]?(AUDIT_DONE|AUDIT_INCOMPLETE|IMPLEMENT_DONE|IMPLEMENT_BLOCKED|FIX_DONE|FIX_BLOCKED|REVIEW_DONE|SOLVER_DONE|META_JUDGE_DONE|META_RESOLVED|TEST_ADD_DONE):" "$file" 2>/dev/null \
    | grep -vE "<reason>|<id>|<status>|<category>|<framing>|<role>|<verdict>|round-N" \
    | sed -E 's/^[+]+//' \
    | tail -1 \
    | head -c 180 \
    || true
}

route_hint() {
  local marker="$1"
  case "$marker" in
    IMPLEMENT_DONE:*:ok*) echo "commit/push/open PR, then spawn 3 reviewers" ;;
    IMPLEMENT_DONE:*:partial|IMPLEMENT_DONE:*:blocked) echo "inspect blocker, then re-prompt or reflect" ;;
    IMPLEMENT_BLOCKED:*) echo "inspect blocker, then meta-reflect" ;;
    FIX_DONE:*) echo "commit/push, then spawn reviewer round r+1" ;;
    FIX_BLOCKED:*) echo "meta-reflect before more fix work" ;;
    REVIEW_DONE:*:approve) echo "wait for remaining reviewers; merge only with consensus and green checks" ;;
    REVIEW_DONE:*:comment) echo "advisory review; wait for remaining reviewers" ;;
    REVIEW_DONE:*:reject) echo "wait for remaining reviewers, then spawn fix round r+1" ;;
    SOLVER_DONE:*) echo "wait for 3 solvers in the same round, then spawn meta-judge" ;;
    META_JUDGE_DONE:consensus:*) echo "spawn implementation codex" ;;
    META_JUDGE_DONE:converge:*) echo "spawn next solver round with convergence framing" ;;
    META_JUDGE_DONE:escalate:*) echo "spawn reflector or re-judge legacy escalation output" ;;
    META_RESOLVED:retry-fix:*) echo "spawn implement or fix codex, depending on PR state" ;;
    META_RESOLVED:re-design:*) echo "close stale PR path and start a fresh design round" ;;
    META_RESOLVED:re-cluster:*) echo "close stale PR path and re-run audit split" ;;
    META_RESOLVED:drop:*) echo "close issue as intentionally dropped" ;;
    META_RESOLVED:escalate-human:*) echo "only now label human-blocked and post a decision banner" ;;
    AUDIT_DONE:*) echo "validate evidence, open design issues, then dispatch implementation" ;;
    AUDIT_INCOMPLETE:*) echo "re-dispatch audit for missing evidence" ;;
    TEST_ADD_DONE:*) echo "commit/push test additions and wait for CI" ;;
    *) echo "" ;;
  esac
}

print_process_snapshot() {
  local repo_scoped=0
  local loop_like=0
  local direct_codex=0
  local line
  local scoped_lines=()
  local unscoped_lines=()

  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
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
        unscoped_lines+=("$line")
      fi
    fi
  done < <(ps -eo command= 2>/dev/null || ps -ef 2>/dev/null || true)

  echo "active_codex:"
  echo "  repo_scoped_spawn_codex=$repo_scoped"
  echo "  loop_like_spawn_codex=$loop_like"
  echo "  direct_codex_exec=$direct_codex"

  if [[ "${#scoped_lines[@]}" -gt 0 ]]; then
    echo "active_repo_scoped_logs:"
    printf '%s\n' "${scoped_lines[@]}" \
      | sed -E 's/[[:space:]]+/ /g; s#.*--log ([^ ]*/)?([^/ ]+)\.log.*#  \2#' \
      | sort \
      | head -10
  fi

  if [[ "$repo_scoped" -eq 0 && "${#unscoped_lines[@]}" -gt 0 ]]; then
    echo "unscoped_loop_like_processes:"
    printf '  %s\n' "${unscoped_lines[@]}" | sed -E 's/[[:space:]]+/ /g' | head -5
  fi
}

print_active_logs() {
  if [[ ! -d "$log_dir" ]]; then
    echo "active_logs_last_10m:"
    echo "  unavailable: logs directory missing"
    return
  fi

  local found=0
  local file
  echo "active_logs_last_10m:"
  while IFS= read -r file; do
    [[ -f "$file" ]] || continue
    [[ "$(tail_exit_code "$file")" == "none" ]] || continue
    found=1
    echo "  $(basename "$file")"
  done < <(find "$log_dir" -name "*.log" -type f -mmin -10 2>/dev/null | sort || true)

  if [[ "$found" -eq 0 ]]; then
    echo "  none"
  fi
}

print_recent_markers() {
  if [[ ! -d "$log_dir" ]]; then
    echo "recent_finished_markers_last_60m:"
    echo "  unavailable: logs directory missing"
    return
  fi

  local found=0
  local file
  local exit_code
  local marker
  local hint
  echo "recent_finished_markers_last_60m:"
  while IFS= read -r file; do
    [[ -f "$file" ]] || continue
    exit_code="$(tail_exit_code "$file")"
    [[ "$exit_code" != "none" ]] || continue
    marker="$(marker_from_log "$file")"
    [[ -n "$marker" ]] || continue
    found=1
    echo "  $(basename "$file"): exit=$exit_code marker=$marker"
    if [[ "$exit_code" == "0" ]]; then
      hint="$(route_hint "$marker")"
      [[ -z "$hint" ]] || echo "    next=$hint"
    else
      echo "    next=inspect failed log before routing"
    fi
  done < <(find "$log_dir" -name "*.log" -type f -mmin -60 2>/dev/null | sort || true)

  if [[ "$found" -eq 0 ]]; then
    echo "  none"
  fi
}

print_visible_loop_log_summaries() {
  if [[ ! -d "$log_dir" ]]; then
    echo "visible_loop_log_summaries_last_60m:"
    echo "  unavailable: logs directory missing"
    return
  fi

  if [[ ! -x "$log_summary_script" ]]; then
    echo "visible_loop_log_summaries_last_60m:"
    echo "  unavailable: log summary helper missing path=$log_summary_script"
    return
  fi

  local files=()
  local file
  while IFS= read -r file; do
    files+=("$file")
  done < <(find "$log_dir" -name "visible-*.log" -type f -mmin -60 2>/dev/null | sort || true)

  echo "visible_loop_log_summaries_last_60m:"
  if [[ "${#files[@]}" -eq 0 ]]; then
    echo "  none"
    return
  fi

  "$log_summary_script" "${files[@]}" | sed 's/^/  /'
}

print_monitor_snapshot() {
  local monitor_log="$log_dir/concurrency-monitor.log"
  echo "monitor:"
  echo "  alert_log=$(file_status "$refactor_dir/.concurrency-alert.log")"
  echo "  pending_events=$(file_status "$refactor_dir/.controller-pending-events.log")"
  echo "  monitor_state=$(file_status "$refactor_dir/.concurrency-monitor-state.json")"
  if [[ -f "$monitor_log" ]]; then
    local max_zero
    local current_zero
    max_zero="$(tail -10 "$monitor_log" 2>/dev/null | grep -oE "zero_streak=[0-9]+" | sort -t= -k2 -rn | head -1 || true)"
    current_zero="$(tail -1 "$monitor_log" 2>/dev/null | grep -oE "zero_streak=[0-9]+" | head -1 || true)"
    echo "  zero_streak_max_10_ticks=${max_zero:-none}"
    echo "  zero_streak_current=${current_zero:-none}"
  else
    echo "  zero_streak_max_10_ticks=unavailable"
    echo "  zero_streak_current=unavailable"
  fi
}

degraded_reasons=()
[[ "$host_env_status" == "loaded" ]] || degraded_reasons+=("host_env_$host_env_status")
[[ -d "$log_dir" ]] || degraded_reasons+=("logs_missing")
[[ -d "$prompt_dir" ]] || degraded_reasons+=("prompts_missing")

echo "codex_refactor_loop_peek"
echo "timestamp_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "repo_root=$repo_root"
echo "branch=$(git -C "$repo_root" branch --show-current 2>/dev/null || echo unknown)"
echo "host_env=$host_env_status path=$repo_root/.refactor-loop/host.env"
echo "refactor_loop_dir=$(path_status "$refactor_dir")"
echo "logs_dir=$(path_status "$log_dir") file_count=$(count_files "$log_dir")"
echo "prompts_dir=$(path_status "$prompt_dir") file_count=$(count_files "$prompt_dir")"
echo "runs_dir=$(path_status "$run_dir") file_count=$(count_files "$run_dir")"
echo "github_state=skipped local_read_only_peek"

print_process_snapshot
print_active_logs
print_recent_markers
print_visible_loop_log_summaries
print_monitor_snapshot

if [[ "${#degraded_reasons[@]}" -gt 0 ]]; then
  echo "degraded=true"
  printf 'degraded_reasons=%s\n' "$(IFS=,; echo "${degraded_reasons[*]}")"
  if [[ -x "$helper_script" ]]; then
    echo "delegated_helper=$helper_script"
    bash "$helper_script" || true
  else
    echo "delegated_helper=missing path=$helper_script"
  fi
else
  echo "degraded=false"
fi
