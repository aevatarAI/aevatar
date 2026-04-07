#!/usr/bin/env bash
# PR Size Guard — warns when a PR exceeds the file/LOC thresholds.
# Runs in CI on pull_request events. Advisory only (does not block merge).
#
# Thresholds:
#   FILES_THRESHOLD=30   (max changed files, excluding auto-generated)
#   LOC_THRESHOLD=800    (max net lines changed)
#
# Auto-generated files excluded from count:
#   *.Designer.cs, *.g.cs, **/obj/**, **/bin/**, **/*.pb.cs

set -euo pipefail

FILES_THRESHOLD="${PR_SIZE_FILES_THRESHOLD:-30}"
LOC_THRESHOLD="${PR_SIZE_LOC_THRESHOLD:-800}"

# Determine diff target
if [[ -n "${GITHUB_BASE_REF:-}" ]]; then
  git fetch --no-tags --depth=1 origin "${GITHUB_BASE_REF}" 2>/dev/null || true
  DIFF_TARGET="origin/${GITHUB_BASE_REF}...HEAD"
elif [[ -n "${1:-}" ]]; then
  DIFF_TARGET="$1"
else
  DIFF_TARGET="origin/dev...HEAD"
fi

echo "PR Size Guard: comparing ${DIFF_TARGET}"
echo "Thresholds: files=${FILES_THRESHOLD}, LOC=${LOC_THRESHOLD}"

# Count changed files (excluding auto-generated)
CHANGED_FILES=$(git diff --name-only "${DIFF_TARGET}" 2>/dev/null \
  | grep -v '\.Designer\.cs$' \
  | grep -v '\.g\.cs$' \
  | grep -v '\.pb\.cs$' \
  | grep -v '/obj/' \
  | grep -v '/bin/' \
  | wc -l | tr -d ' ')

# Count net LOC changed
STAT_LINE=$(git diff --stat "${DIFF_TARGET}" 2>/dev/null | tail -1)
INSERTIONS=$(echo "${STAT_LINE}" | grep -oE '[0-9]+ insertion' | grep -oE '[0-9]+' || echo "0")
DELETIONS=$(echo "${STAT_LINE}" | grep -oE '[0-9]+ deletion' | grep -oE '[0-9]+' || echo "0")
NET_LOC=$(( INSERTIONS - DELETIONS ))
# Use absolute value for comparison
ABS_LOC=${NET_LOC#-}

echo "Result: ${CHANGED_FILES} files changed, +${INSERTIONS}/-${DELETIONS} (net ${NET_LOC})"

EXIT_CODE=0

if [[ "${CHANGED_FILES}" -gt "${FILES_THRESHOLD}" ]]; then
  echo ""
  echo "::warning::PR exceeds file threshold: ${CHANGED_FILES} files changed (limit: ${FILES_THRESHOLD}). Consider splitting this PR."
  EXIT_CODE=1
fi

if [[ "${ABS_LOC}" -gt "${LOC_THRESHOLD}" ]]; then
  echo ""
  echo "::warning::PR exceeds LOC threshold: ${ABS_LOC} net lines (limit: ${LOC_THRESHOLD}). Consider splitting this PR."
  EXIT_CODE=1
fi

if [[ "${EXIT_CODE}" -eq 0 ]]; then
  echo "PR size OK."
fi

# Advisory only — always exit 0 so we don't block merge.
# To make this blocking, change the line below to: exit ${EXIT_CODE}
exit 0
