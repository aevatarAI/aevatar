#!/usr/bin/env bash
# Read-only file-count guard for the visible-loop process automation batch.
#
# Counts only modified tracked files and prospective tracked files under the
# codex-refactor-loop skill directory. It deliberately excludes backend,
# frontend, architecture docs, and shared dirty workspace files by path scope.

set -euo pipefail

usage() {
  echo "usage: visible-loop-batch-files.sh [minimum-count]" >&2
  echo "   or: visible-loop-batch-files.sh --min <minimum-count>" >&2
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(git -C "$script_dir" rev-parse --show-toplevel 2>/dev/null || git rev-parse --show-toplevel 2>/dev/null || true)"
minimum_count=10
scope_path=".claude/skills/codex-refactor-loop"
status=0
batch_files=()

case "${1:-}" in
  -h|--help)
    usage
    exit 0
    ;;
  --min)
    if [[ $# -ne 2 ]]; then
      usage
      exit 2
    fi
    minimum_count="$2"
    ;;
  "")
    ;;
  *)
    if [[ $# -ne 1 ]]; then
      usage
      exit 2
    fi
    minimum_count="$1"
    ;;
esac

if [[ -z "$repo_root" ]]; then
  echo "visible_loop_batch_files"
  echo "status=blocked"
  echo "reason=not inside a git repository"
  exit 2
fi

if [[ ! "$minimum_count" =~ ^[0-9]+$ || "$minimum_count" -eq 0 ]]; then
  echo "minimum count must be a positive integer: $minimum_count" >&2
  exit 2
fi

while IFS= read -r file; do
  [[ -n "$file" ]] || continue
  batch_files+=("$file")
done < <(git -C "$repo_root" ls-files -m -o --exclude-standard "$scope_path" | sort)

file_count="${#batch_files[@]}"

echo "visible_loop_batch_files"
echo "repo_root=$repo_root"
echo "scope=$scope_path"
echo "excluded_scopes=backend,frontend,architecture_docs,shared_dirty_workspace"
echo "minimum_count=$minimum_count"
echo "file_count=$file_count"
echo "files:"
if [[ "$file_count" -eq 0 ]]; then
  echo "  none"
else
  printf '  %s\n' "${batch_files[@]}"
fi

if (( file_count < minimum_count )); then
  echo "threshold_status=below"
  echo "shortfall=$((minimum_count - file_count))"
  status=1
else
  echo "threshold_status=reached"
  echo "shortfall=0"
fi

exit "$status"
