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

FAKE_REPO="${TMP_DIR}/repo"
FAKE_BIN="${TMP_DIR}/bin"
mkdir -p "${FAKE_REPO}/.claude/skills/codex-refactor-loop/prompts" "${FAKE_BIN}"
cp "${TEMPLATE}" "${FAKE_REPO}/.claude/skills/codex-refactor-loop/prompts/triage-external-issue.md"

cat > "${FAKE_BIN}/gh" <<'FAKE_GH'
#!/usr/bin/env bash
if [ "$1" = "issue" ] && [ "$2" = "list" ]; then
  printf '999 testuser\n'
  exit 0
fi
echo "unexpected gh call: $*" >&2
exit 2
FAKE_GH
chmod +x "${FAKE_BIN}/gh"

PATH="${FAKE_BIN}:${PATH}" \
REPO_ROOT="${FAKE_REPO}" \
TRIAGE_MONITOR_RUN_ONCE=1 \
bash "${REPO_ROOT}/.claude/skills/codex-refactor-loop/scripts/triage-monitor.sh" \
  > "${TMP_DIR}/triage-monitor.out"

PENDING="${FAKE_REPO}/.refactor-loop/.controller-pending-events.log"
STATE="${FAKE_REPO}/.refactor-loop/triage-monitor-state.json"
PROMPT="${FAKE_REPO}/.refactor-loop/prompts/triage-issue-999.md"
LOG="${FAKE_REPO}/.refactor-loop/logs/triage-issue-999.log"

test -f "${PENDING}" || fail "pending controller event was not written"
grep -Fq "new-triage-issue 999 testuser prompt=${PROMPT} log=${LOG} timeout=5400" "${PENDING}" \
  || fail "pending controller event does not carry spawn-codex inputs"
test -f "${PROMPT}" || fail "triage prompt was not materialized"
jq -e '."999"' "${STATE}" >/dev/null || fail "issue was not marked seen"
grep -Fq "queued: triage codex for issue #999" "${TMP_DIR}/triage-monitor.out" \
  || fail "triage monitor did not log queued controller spawn request"
if sed '/^[[:space:]]*#/d' "${REPO_ROOT}/.claude/skills/codex-refactor-loop/scripts/triage-monitor.sh" \
  | grep -Eq 'nohup .*spawn-codex|disown'; then
  fail "triage-monitor still contains detached spawn-codex path"
fi

echo "triage-monitor envsubst tests passed."
