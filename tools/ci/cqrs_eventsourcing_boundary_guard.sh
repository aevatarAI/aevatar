#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

allowlist_file="tools/ci/cqrs_eventsourcing_legacy_allowlist.txt"

if [[ ! -f "${allowlist_file}" ]]; then
  echo "Missing allowlist: ${allowlist_file}"
  exit 1
fi

read_side_file_regex='/(Queries|ReadPorts)/|/(I?[A-Za-z0-9_]*(Query(Service|Reader|Client)?|BindingReader|ReadPort|SnapshotPort))\.cs$'
read_side_files=()

while IFS= read -r read_side_file; do
  [[ -z "${read_side_file}" ]] && continue
  read_side_files+=("${read_side_file}")
done < <(
  rg --files \
    src/Aevatar.Scripting.Application \
    src/Aevatar.Scripting.Infrastructure \
    src/Aevatar.Scripting.Projection \
    src/workflow/Aevatar.Workflow.Application \
    src/workflow/Aevatar.Workflow.Infrastructure \
    src/workflow/Aevatar.Workflow.Projection \
    -g '*.cs' \
    | rg "${read_side_file_regex}" \
    || true
)

if [[ "${#read_side_files[@]}" -gt 0 ]]; then
  read_side_event_store_hits="$(
    rg -n "IEventStore|GetEventsAsync\\(|GetVersionAsync\\(|AppendAsync\\(|DeleteEventsUpToAsync\\(" \
      "${read_side_files[@]}" \
      || true
  )"

  if [[ -n "${read_side_event_store_hits}" ]]; then
    echo "${read_side_event_store_hits}"
    echo "Read/query paths must not read or replay committed facts from IEventStore. Query must resolve from read models."
    exit 1
  fi

  read_side_writeback_hits="$(
    rg -n "\\.(UpsertAsync|MutateAsync)\\(" \
      "${read_side_files[@]}" \
      || true
  )"

  if [[ -n "${read_side_writeback_hits}" ]]; then
    echo "${read_side_writeback_hits}"
    echo "Read/query paths must not materialize or mutate read models inline. Projection materialization must stay off the query call stack."
    exit 1
  fi
fi

legacy_direct_query_pattern='IStreamRequestReplyClient|RuntimeStreamRequestReplyClient|RuntimeScriptActorQueryClient|RuntimeScriptCatalogQueryService|RuntimeScriptDefinitionSnapshotPort|ScriptActorQueryEnvelopeFactory|ScriptActorQueryRouteConventions|RuntimeWorkflowQueryClient|RuntimeWorkflowActorBindingReader|WorkflowActorBindingQueryEnvelopeFactory|QueryActorAsync<|Query[A-Za-z0-9_]+RequestedEvent'

legacy_direct_query_hits="$(
  rg -n "${legacy_direct_query_pattern}" \
    src \
    -g '*.cs' \
    -g '*.proto' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"

allowlist_entries="$(
  sed '/^[[:space:]]*#/d;/^[[:space:]]*$/d' "${allowlist_file}"
)"

legacy_direct_query_files=""
legacy_direct_query_disallowed=""

while IFS= read -r hit; do
  [[ -z "${hit}" ]] && continue

  file_path="${hit%%:*}"
  legacy_direct_query_files="${legacy_direct_query_files}${file_path}"$'\n'

  if ! printf '%s\n' "${allowlist_entries}" | rg -Fx "${file_path}" >/dev/null; then
    legacy_direct_query_disallowed="${legacy_direct_query_disallowed}${hit}"$'\n'
  fi
done <<< "${legacy_direct_query_hits}"

if [[ -n "${legacy_direct_query_disallowed}" ]]; then
  echo "Detected direct actor query/request-reply usage outside allowlist:"
  printf '%s' "${legacy_direct_query_disallowed}"
  echo "Direct actor query/request-reply must not expand. Migrate to read models or eventized continuation, or explicitly retire existing debt before adding new usage."
  exit 1
fi

current_legacy_files="$(printf '%s' "${legacy_direct_query_files}" | sort -u)"
stale_allowlist_entries=""

while IFS= read -r allowlist_entry; do
  [[ -z "${allowlist_entry}" ]] && continue

  if ! printf '%s\n' "${current_legacy_files}" | rg -Fx "${allowlist_entry}" >/dev/null; then
    stale_allowlist_entries="${stale_allowlist_entries}${allowlist_entry}"$'\n'
  fi
done <<< "${allowlist_entries}"

if [[ -n "${stale_allowlist_entries}" ]]; then
  echo "Stale CQRS/EventSourcing legacy allowlist entries detected:"
  printf '%s' "${stale_allowlist_entries}"
  echo "Remove stale entries from ${allowlist_file} so the allowlist only documents live debt."
  exit 1
fi

echo "CQRS/EventSourcing boundary guard passed."
