#!/usr/bin/env bash
# Read-only guard for visible-loop worker prompts.
#
# The guard checks only prompt text. It does not write loop state, call GitHub,
# or modify tracked files. Marker template examples are allowed; unresolved
# placeholders elsewhere are treated as readiness failures.

set -euo pipefail

usage() {
  echo "usage: visible-loop-prompt-guard.sh [prompt-file...]" >&2
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(git -C "$script_dir" rev-parse --show-toplevel 2>/dev/null || git rev-parse --show-toplevel 2>/dev/null || true)"
fallback_prompt_dir="/Users/abigaildeng/Documents/Playground/aevatar/.refactor-loop/prompts"
sentinel="⟦AI:AUTO-LOOP⟧"
status=0
prompt_files=()

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

discover_prompt_files() {
  local dir
  local file

  for dir in \
    "${repo_root:-}/.refactor-loop/prompts" \
    "$fallback_prompt_dir"
  do
    [[ -n "$dir" && -d "$dir" ]] || continue
    while IFS= read -r file; do
      prompt_files+=("$file")
    done < <(find "$dir" -maxdepth 1 -type f -name "visible-*.md" | sort)

    if [[ "${#prompt_files[@]}" -gt 0 ]]; then
      echo "prompt_dir=$dir"
      return 0
    fi
  done

  return 1
}

if [[ $# -gt 0 ]]; then
  prompt_files=("$@")
else
  discover_prompt_files || true
fi

echo "visible_loop_prompt_guard"
echo "repo_root=${repo_root:-unknown}"

if [[ "${#prompt_files[@]}" -eq 0 ]]; then
  echo "status=skipped"
  echo "reason=no_visible_prompt_files_discovered"
  exit 0
fi

check_placeholders() {
  local file="$1"

  awk '
    function allowed_placeholder_line(line) {
      if (line ~ /(VISIBLE_WORKER_DONE|SOLVER_DONE|REVIEW_DONE|META_JUDGE_DONE|META_RESOLVED|IMPLEMENT_DONE|IMPLEMENT_BLOCKED|FIX_DONE|FIX_BLOCKED|AUDIT_DONE|AUDIT_INCOMPLETE|TEST_ADD_DONE):/) {
        return 1
      }
      if (line ~ /Completion marker/) {
        return 1
      }
      if (line ~ /prompt echo|placeholder|template-only|marker example/) {
        return 1
      }
      if (line ~ /`<\.\.\.>`/) {
        return 1
      }
      return 0
    }

    {
      if (($0 ~ /\{\{[^}][^}]*\}\}/ || $0 ~ /<[A-Za-z][A-Za-z0-9_.|\/-]*>/) && !allowed_placeholder_line($0)) {
        print FILENAME ":" FNR ": violation=unresolved_placeholder line=" $0
      }
    }
  ' "$file"
}

check_raw_mentions() {
  local file="$1"

  awk '
    {
      if ($0 ~ /(^|[^[:alnum:]_.%+-])@[[:alnum:]][[:alnum:]_-]+/) {
        print FILENAME ":" FNR ": violation=risky_raw_at_mention line=" $0
      }
    }
  ' "$file"
}

mentions_external_content() {
  local file="$1"

  grep -Eiq '(GitHub|pull[ -]request|external-facing|comment|body|issue|banner|[^[:alpha:]]PR[^[:alpha:]])' "$file"
}

check_prompt_file() {
  local file="$1"
  local file_status=0
  local placeholder_output
  local mention_output

  echo "file=$file"

  if [[ ! -f "$file" ]]; then
    echo "result=prompt_file failed reason=not_found path=$file"
    return 1
  fi

  placeholder_output="$(check_placeholders "$file")"
  if [[ -n "$placeholder_output" ]]; then
    printf '%s\n' "$placeholder_output"
    file_status=1
  else
    echo "placeholders=ok"
  fi

  mention_output="$(check_raw_mentions "$file")"
  if [[ -n "$mention_output" ]]; then
    printf '%s\n' "$mention_output"
    file_status=1
  else
    echo "raw_at_mentions=ok"
  fi

  if mentions_external_content "$file"; then
    echo "external_content_reference=detected"
    if grep -Fq "$sentinel" "$file"; then
      echo "auto_loop_sentinel=ok"
    else
      echo "violation=missing_auto_loop_sentinel sentinel=$sentinel"
      file_status=1
    fi
  else
    echo "auto_loop_sentinel=skipped reason=no_external_content_reference"
  fi

  if [[ "$file_status" -eq 0 ]]; then
    echo "result=prompt_file ok path=$file"
  else
    echo "result=prompt_file failed path=$file"
  fi

  return "$file_status"
}

for prompt_file in "${prompt_files[@]}"; do
  if ! check_prompt_file "$prompt_file"; then
    status=1
  fi
done

if [[ "$status" -eq 0 ]]; then
  echo "visible_loop_prompt_guard_result=ok"
else
  echo "visible_loop_prompt_guard_result=failed"
fi

exit "$status"
