#!/usr/bin/env bash
#
# Frontend static boundary guard.
#
# Prevents frontend code from violating CQRS / Actor architecture boundaries
# defined in CLAUDE.md and docs/canon/cqrs-projection.md:
#
#   1. Calling actor-state, event-replay, or projection-refresh endpoints
#      (queries must go through readmodel, not replay or refresh).
#   2. Parsing actorId string prefixes for business semantics
#      (actorId is an opaque address per CLAUDE.md "actorId 对调用方不透明").
#   3. Accessing EventEnvelope route/runtime/propagation internals
#      (envelope internals are delivery context, not business completion semantics).
#
# Failure mode: a frontend PR introduces direct actor-state reads, event-replay
# polling, or actorId prefix parsing; CI fails fast with the offending file.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

FRONTEND_SRC="apps/aevatar-console-web/src"

if [[ ! -d "${FRONTEND_SRC}" ]]; then
  echo "frontend_static_boundary_guard: skipped (${FRONTEND_SRC} does not exist)"
  exit 0
fi

FRONTEND_EXCLUDES=(
  -g '!**/*.test.*'
  -g '!**/__tests__/**'
  -g '!**/.umi*/**'
  -g '!**/node_modules/**'
)

failures=0

forbidden_route_hits="$(
  rg -n '(/actor-state|/events/replay|/projections?/refresh)' "${FRONTEND_SRC}" \
    -g '*.ts' -g '*.tsx' -g '*.js' -g '*.jsx' \
    "${FRONTEND_EXCLUDES[@]}" \
    || true
)"

if [[ -n "${forbidden_route_hits}" ]]; then
  echo "${forbidden_route_hits}"
  echo "Frontend code must not call actor-state, event replay, or projection refresh endpoints."
  failures=1
fi

actor_id_parsing_hits="$(
  rg -n -P '\b[\w$]*[Aa]ctorId\b\s*\??\s*\.\s*(startsWith|includes|indexOf|match|split)\s*\(' "${FRONTEND_SRC}" \
    -g '*.ts' -g '*.tsx' -g '*.js' -g '*.jsx' \
    "${FRONTEND_EXCLUDES[@]}" \
    || true
)"

if [[ -n "${actor_id_parsing_hits}" ]]; then
  echo "${actor_id_parsing_hits}"
  echo "Frontend code must treat actorId as an opaque address, not parse it for business semantics."
  failures=1
fi

event_envelope_field_hits="$(
  rg -n -P '\b(eventEnvelope|actorEnvelope|runtimeEnvelope|workflowRunEventEnvelope)\b\s*\??\s*\.\s*(route|runtime|propagation)\b' "${FRONTEND_SRC}" \
    -g '*.ts' -g '*.tsx' -g '*.js' -g '*.jsx' \
    "${FRONTEND_EXCLUDES[@]}" \
    || true
)"

if [[ -n "${event_envelope_field_hits}" ]]; then
  echo "${event_envelope_field_hits}"
  echo "Frontend code must not branch on EventEnvelope route/runtime/propagation internals."
  failures=1
fi

if [[ "${failures}" -ne 0 ]]; then
  echo "frontend_static_boundary_guard: failed"
  exit 1
fi

echo "frontend_static_boundary_guard: ok"
