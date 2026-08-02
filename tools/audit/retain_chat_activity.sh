#!/usr/bin/env bash
set -euo pipefail

mode=dry-run
if (( $# > 1 )); then
  printf 'usage: %s [--dry-run|--execute]\n' "$0" >&2
  exit 2
fi
case "${1:---dry-run}" in
  --dry-run) ;;
  --execute) mode=execute ;;
  *)
    printf 'usage: %s [--dry-run|--execute]\n' "$0" >&2
    exit 2
    ;;
esac

: "${AEVATAR_ELASTICSEARCH_URL:?AEVATAR_ELASTICSEARCH_URL is required}"
alias_name="${AEVATAR_AUDIT_INDEX_ALIAS:-aevatar-audit-trail-current}"
[[ "${alias_name}" =~ ^[a-zA-Z0-9._-]+$ ]] || {
  printf 'AEVATAR_AUDIT_INDEX_ALIAS contains unsupported characters\n' >&2
  exit 2
}

query=''
read -r -d '' query <<'JSON' || true
{
  "query": {
    "bool": {
      "filter": [
        { "range": { "artifact.recorded_at": { "lt": "now-30d/d" } } },
        { "exists": { "field": "artifact.record.provenance.chat.surface" } }
      ]
    }
  }
}
JSON

base_url="${AEVATAR_ELASTICSEARCH_URL%/}"
curl_args=(--fail-with-body --silent --show-error --request POST --header 'Content-Type: application/json')
run_curl() {
  if [[ -n "${AEVATAR_ELASTICSEARCH_API_KEY:-}" ]]; then
    printf 'Authorization: ApiKey %s\n' "${AEVATAR_ELASTICSEARCH_API_KEY}" |
      env -u AEVATAR_ELASTICSEARCH_API_KEY curl "${curl_args[@]}" --header @- "$@"
  else
    curl "${curl_args[@]}" --netrc "$@"
  fi
}

SECONDS=0
trap 'printf "status=failure\n" >&2' ERR
printf 'mode=%s\nalias=%s\ncutoff=now-30d/d\n' "${mode}" "${alias_name}"

if [[ "${mode}" == dry-run ]]; then
  response="$(run_curl --data-binary "${query}" "${base_url}/${alias_name}/_count")"
  matched_count="$(jq -er '.count | numbers' <<<"${response}")"
  printf 'matched_count=%s\nduration_seconds=%s\nstatus=success\n' "${matched_count}" "${SECONDS}"
  exit 0
fi

response="$(run_curl --data-binary "${query}" "${base_url}/${alias_name}/_delete_by_query?conflicts=proceed&wait_for_completion=true&refresh=false")"
jq -e '.timed_out == false and ((.failures // []) | length == 0)' <<<"${response}" >/dev/null
deleted_count="$(jq -er '.deleted | numbers' <<<"${response}")"
duration_ms="$(jq -er '.took | numbers' <<<"${response}")"
printf 'deleted_count=%s\nduration_ms=%s\nduration_seconds=%s\nstatus=success\n' "${deleted_count}" "${duration_ms}" "${SECONDS}"
