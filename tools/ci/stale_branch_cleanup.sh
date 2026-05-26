#!/usr/bin/env bash
# Stale Branch Cleanup — deletes remote branches that are already merged into dev.
# Safety: ONLY deletes branches whose commits are fully reachable from dev.
# Protected branches (master, dev, main, release/*) are never deleted.
#
# Usage:
#   bash tools/ci/stale_branch_cleanup.sh          # dry-run (default)
#   bash tools/ci/stale_branch_cleanup.sh --apply   # actually delete

set -euo pipefail

DRY_RUN=true
if [[ "${1:-}" == "--apply" ]]; then
  DRY_RUN=false
fi

TARGET_BRANCH="${CLEANUP_TARGET_BRANCH:-dev}"

echo "Stale Branch Cleanup"
echo "Target: origin/${TARGET_BRANCH}"
echo "Mode: $(${DRY_RUN} && echo 'DRY RUN' || echo 'APPLY')"
echo ""

git fetch --prune origin 2>/dev/null || true

# Protected branch patterns — never delete these
PROTECTED_PATTERN="^origin/(master|dev|main|release/)"

MERGED_BRANCHES=$(git branch -r --merged "origin/${TARGET_BRANCH}" 2>/dev/null \
  | sed 's/^ *//' \
  | grep '^origin/' \
  | grep -v "origin/HEAD" \
  | grep -Ev "${PROTECTED_PATTERN}" \
  || true)

if [[ -z "${MERGED_BRANCHES}" ]]; then
  echo "No stale merged branches found."
  exit 0
fi

COUNT=$(echo "${MERGED_BRANCHES}" | wc -l | tr -d ' ')
echo "Found ${COUNT} merged branches:"
echo ""

while IFS= read -r branch; do
  REMOTE_NAME="${branch#origin/}"
  LAST_COMMIT_DATE=$(git log -1 --format="%ai" "${branch}" 2>/dev/null | cut -d' ' -f1)
  LAST_AUTHOR=$(git log -1 --format="%an" "${branch}" 2>/dev/null)

  if ${DRY_RUN}; then
    echo "  [DRY RUN] would delete: ${REMOTE_NAME} (last: ${LAST_COMMIT_DATE} by ${LAST_AUTHOR})"
  else
    echo "  Deleting: ${REMOTE_NAME} (last: ${LAST_COMMIT_DATE} by ${LAST_AUTHOR})"
    git push origin --delete "${REMOTE_NAME}" 2>/dev/null || echo "    Failed to delete ${REMOTE_NAME}"
  fi
done <<< "${MERGED_BRANCHES}"

echo ""
if ${DRY_RUN}; then
  echo "Dry run complete. Run with --apply to delete."
else
  echo "Cleanup complete. ${COUNT} branches deleted."
fi
