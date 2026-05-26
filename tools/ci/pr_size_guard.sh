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

resolve_diff_target_from_shas() {
  local base_sha="$1"
  local head_sha="$2"

  if ! git cat-file -e "${base_sha}^{commit}" >/dev/null 2>&1; then
    echo "::warning::PR Size Guard could not resolve base commit ${base_sha}; falling back to ref-based diff." >&2
    return 1
  fi

  if ! git cat-file -e "${head_sha}^{commit}" >/dev/null 2>&1; then
    echo "::warning::PR Size Guard could not resolve head commit ${head_sha}; falling back to ref-based diff." >&2
    return 1
  fi

  echo "${base_sha}...${head_sha}"
}

resolve_diff_target() {
  local base_ref="$1"
  local triple_dot="origin/${base_ref}...HEAD"

  git fetch --no-tags origin \
    "+refs/heads/${base_ref}:refs/remotes/origin/${base_ref}" \
    2>/dev/null || true

  if ! git rev-parse --verify "origin/${base_ref}" >/dev/null 2>&1; then
    echo "::warning::PR Size Guard could not resolve origin/${base_ref}; falling back to HEAD only." >&2
    echo "HEAD"
    return
  fi

  if ! git merge-base "origin/${base_ref}" HEAD >/dev/null 2>&1; then
    echo "::warning::PR Size Guard could not find a merge base for ${triple_dot}; falling back to HEAD only." >&2
    echo "HEAD"
    return
  fi

  echo "${triple_dot}"
}

# Determine diff target
if [[ -n "${GITHUB_BASE_SHA:-}" && -n "${GITHUB_HEAD_SHA:-}" ]]; then
  DIFF_TARGET=$(resolve_diff_target_from_shas "${GITHUB_BASE_SHA}" "${GITHUB_HEAD_SHA}") || DIFF_TARGET=$(resolve_diff_target "${GITHUB_BASE_REF:-dev}")
elif [[ -n "${GITHUB_BASE_REF:-}" ]]; then
  DIFF_TARGET=$(resolve_diff_target "${GITHUB_BASE_REF}")
elif [[ -n "${1:-}" ]]; then
  DIFF_TARGET="$1"
else
  DIFF_TARGET=$(resolve_diff_target "dev")
fi

echo "PR Size Guard: comparing ${DIFF_TARGET}"
echo "Thresholds: files=${FILES_THRESHOLD}, LOC=${LOC_THRESHOLD}"

DIFF_NUMSTAT=$(git diff --numstat "${DIFF_TARGET}" 2>/dev/null || true)
if [[ -z "${DIFF_NUMSTAT}" && "${DIFF_TARGET}" != "HEAD" ]]; then
  echo "::warning::PR Size Guard could not evaluate ${DIFF_TARGET}; defaulting to zero diff so the advisory check does not fail CI." >&2
fi

CHANGED_FILES=$(printf '%s\n' "${DIFF_NUMSTAT}" \
  | awk '
      NF == 3 &&
      $3 !~ /\.Designer\.cs$/ &&
      $3 !~ /\.g\.cs$/ &&
      $3 !~ /\.pb\.cs$/ &&
      $3 !~ /\/obj\// &&
      $3 !~ /\/bin\// { count++ }
      END { print count + 0 }
    ')

INSERTIONS=$(printf '%s\n' "${DIFF_NUMSTAT}" \
  | awk '
      NF == 3 &&
      $3 !~ /\.Designer\.cs$/ &&
      $3 !~ /\.g\.cs$/ &&
      $3 !~ /\.pb\.cs$/ &&
      $3 !~ /\/obj\// &&
      $3 !~ /\/bin\// &&
      $1 ~ /^[0-9]+$/ { sum += $1 }
      END { print sum + 0 }
    ')

DELETIONS=$(printf '%s\n' "${DIFF_NUMSTAT}" \
  | awk '
      NF == 3 &&
      $3 !~ /\.Designer\.cs$/ &&
      $3 !~ /\.g\.cs$/ &&
      $3 !~ /\.pb\.cs$/ &&
      $3 !~ /\/obj\// &&
      $3 !~ /\/bin\// &&
      $2 ~ /^[0-9]+$/ { sum += $2 }
      END { print sum + 0 }
    ')

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
