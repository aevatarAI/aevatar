#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SCRIPT="${ROOT_DIR}/tools/audit/retain_chat_activity.sh"
TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-chat-retention-tests.XXXXXX")"
trap 'rm -rf "${TMP_DIR}"' EXIT

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
assert_eq() { [[ "$1" == "$2" ]] || fail "expected '$2', got '$1'"; }
assert_has() { grep -Fq -- "$2" "$1" || fail "$1 does not contain $2"; }
assert_lacks() { ! grep -Fq -- "$2" "$1" || fail "$1 contains forbidden value $2"; }

[[ -f "${SCRIPT}" ]] || fail "missing ${SCRIPT}"
command -v jq >/dev/null || fail 'jq is required'

mkdir -p "${TMP_DIR}/bin"
cat >"${TMP_DIR}/bin/curl" <<'FAKE_CURL'
#!/usr/bin/env bash
set -euo pipefail
method=GET
url=
body=
: >"${FAKE_CURL_FLAGS}"
printf '%s\n' "$@" >"${FAKE_CURL_ARGV}"
while (( $# )); do
  case "$1" in
    --request|-X) method="$2"; shift 2 ;;
    --data-binary|--data|-d) body="$2"; shift 2 ;;
    --header|-H) shift 2 ;;
    --fail-with-body|--silent|--show-error|--netrc) printf '%s\n' "$1" >>"${FAKE_CURL_FLAGS}"; shift ;;
    *) url="$1"; shift ;;
  esac
done
printf '%s' "${method}" >"${FAKE_CURL_METHOD}"
printf '%s' "${url}" >"${FAKE_CURL_URL}"
printf '%s' "${body}" >"${FAKE_CURL_BODY}"
if [[ "${url}" == */_count ]]; then
  printf '{"count":2,"fakeDocument":"prompt-secret"}\n'
else
  printf '{"deleted":2,"took":7,"timed_out":false,"failures":[],"fakeDocument":"prompt-secret"}\n'
fi
FAKE_CURL
chmod +x "${TMP_DIR}/bin/curl"

export PATH="${TMP_DIR}/bin:${PATH}"
export FAKE_CURL_METHOD="${TMP_DIR}/method"
export FAKE_CURL_URL="${TMP_DIR}/url"
export FAKE_CURL_BODY="${TMP_DIR}/body"
export FAKE_CURL_FLAGS="${TMP_DIR}/flags"
export FAKE_CURL_ARGV="${TMP_DIR}/argv"
export AEVATAR_ELASTICSEARCH_URL=https://elastic.example
export AEVATAR_ELASTICSEARCH_API_KEY=credential-secret

expected_query='{"query":{"bool":{"filter":[{"range":{"artifact.recorded_at":{"lt":"now-30d/d"}}},{"exists":{"field":"artifact.record.provenance.chat.surface"}}]}}}'
canonical_expected="$(jq -cS . <<<"${expected_query}")"

run_success() {
  : >"${TMP_DIR}/stdout"; : >"${TMP_DIR}/stderr"
  bash "${SCRIPT}" "$@" >"${TMP_DIR}/stdout" 2>"${TMP_DIR}/stderr" || fail "retention script failed for: $*"
  assert_eq "$(jq -cS . <"${FAKE_CURL_BODY}")" "${canonical_expected}"
  assert_has "${FAKE_CURL_FLAGS}" '--fail-with-body'
  assert_has "${FAKE_CURL_FLAGS}" '--silent'
  assert_has "${FAKE_CURL_FLAGS}" '--show-error'
  assert_lacks "${TMP_DIR}/stdout" 'prompt-secret'
  assert_lacks "${TMP_DIR}/stderr" 'prompt-secret'
  assert_lacks "${TMP_DIR}/stdout" 'credential-secret'
  assert_lacks "${TMP_DIR}/stderr" 'credential-secret'
  assert_lacks "${FAKE_CURL_ARGV}" 'credential-secret'
}

run_success
assert_eq "$(cat "${FAKE_CURL_METHOD}")" POST
assert_eq "$(cat "${FAKE_CURL_URL}")" 'https://elastic.example/aevatar-audit-trail-current/_count'
assert_has "${TMP_DIR}/stdout" 'mode=dry-run'
assert_has "${TMP_DIR}/stdout" 'alias=aevatar-audit-trail-current'
assert_has "${TMP_DIR}/stdout" 'cutoff=now-30d/d'
assert_has "${TMP_DIR}/stdout" 'matched_count=2'
assert_has "${TMP_DIR}/stdout" 'duration_seconds='
assert_has "${TMP_DIR}/stdout" 'status=success'

run_success --dry-run
assert_eq "$(cat "${FAKE_CURL_URL}")" 'https://elastic.example/aevatar-audit-trail-current/_count'

run_success --execute
assert_eq "$(cat "${FAKE_CURL_METHOD}")" POST
assert_eq "$(cat "${FAKE_CURL_URL}")" 'https://elastic.example/aevatar-audit-trail-current/_delete_by_query?conflicts=proceed&wait_for_completion=true&refresh=false'
assert_has "${TMP_DIR}/stdout" 'mode=execute'
assert_has "${TMP_DIR}/stdout" 'deleted_count=2'
assert_has "${TMP_DIR}/stdout" 'duration_ms=7'

# The body requires typed chat provenance, which this old generic-audit fixture lacks.
old_generic_audit='{"artifact":{"recorded_at":"2026-01-01T00:00:00Z","record":{"operation_name":"generic.audit"}}}'
assert_eq "$(jq -r '.query.bool.filter[] | select(has("exists")) | .exists.field' <"${FAKE_CURL_BODY}")" 'artifact.record.provenance.chat.surface'
if jq -e '.artifact.record.provenance.chat.surface' <<<"${old_generic_audit}" >/dev/null 2>&1; then
  fail 'old generic audit fixture unexpectedly has typed chat provenance'
fi

if bash "${SCRIPT}" --unknown >"${TMP_DIR}/stdout" 2>"${TMP_DIR}/stderr"; then
  fail 'unknown flags must fail'
fi
if env -u AEVATAR_ELASTICSEARCH_URL bash "${SCRIPT}" >"${TMP_DIR}/stdout" 2>"${TMP_DIR}/stderr"; then
  fail 'missing AEVATAR_ELASTICSEARCH_URL must fail'
fi
if AEVATAR_AUDIT_INDEX_ALIAS='../unsafe' bash "${SCRIPT}" >"${TMP_DIR}/stdout" 2>"${TMP_DIR}/stderr"; then
  fail 'unsafe aliases must fail'
fi

printf 'chat activity retention contract tests passed\n'
