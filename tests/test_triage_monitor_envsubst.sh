#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
TEMPLATE="${REPO_ROOT}/.claude/skills/codex-refactor-loop/prompts/triage-external-issue.md"
TMP_DIR="$(mktemp -d /tmp/triage-monitor-envsubst-test.XXXXXXXX)"
TMP="${TMP_DIR}/rendered.md"

cleanup() {
  rm -rf "${TMP_DIR}"
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

{
  cat "${TEMPLATE}"
  printf '\nAuthor: ${AUTHOR}\n'
} | ISSUE_NUMBER=999 AUTHOR=testuser envsubst \
  | sed "s/#560/#999/g; s/issue 560/issue 999/g; s/Author: .*/Author: testuser/g" \
  > "${TMP}"

if grep -Fq '${ISSUE_NUMBER}' "${TMP}"; then
  fail "${TMP} still contains \${ISSUE_NUMBER}"
fi

if grep -Fq '${AUTHOR}' "${TMP}"; then
  fail "${TMP} still contains \${AUTHOR}"
fi

grep -Fq '#999' "${TMP}" || fail "#999 not substituted"
grep -Fq 'testuser' "${TMP}" || fail "testuser not substituted"

echo "triage-monitor envsubst tests passed."
