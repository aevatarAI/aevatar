#!/usr/bin/env bash
#
# Inspector tier boundary guard.
#
# Enforces ADR 0023 (Two-tier Inspector architecture):
#   Tier 1 (canonical state) is served from readmodels via query ports.
#   Tier 2 (observation) is OTel ActivityListener → BoundedChannel<TelemetryFrame>
#   → SSE stream `/api/inspector/events`, and ONLY that one endpoint.
#
# Forbidden patterns inside demos/Aevatar.Demos.Inspector*:
#   1. Any HTTP endpoint NOT scoped to `/api/inspector/events` that references
#      TelemetryFrame or the telemetry channel. Such an endpoint would let a
#      query result be served from observation state, violating "事实源唯一" +
#      "查询走 readmodel".
#   2. Any method signature returning a *collection* of TelemetryFrame
#      (IEnumerable / IReadOnlyList / IReadOnlyCollection / IList / List /
#      ICollection / TelemetryFrame[]). Tier 2 is a live broadcaster — a
#      collection-typed return means historical replay, which is forbidden.
#      `IAsyncEnumerable<TelemetryFrame>` and `ChannelReader<TelemetryFrame>`
#      are the legitimate SSE streaming shapes and are intentionally NOT
#      matched.
#
# Pre-implementation: when no Inspector demo directory exists, the guard
# is a no-op. This lets the guard scaffold land before Phase B writes
# code, so the boundary rule is in place from day one.
#
# Usage:
#   tools/ci/inspector_tier_boundary_guard.sh
#       Scan the production paths under demos/Aevatar.Demos.Inspector*.
#
#   tools/ci/inspector_tier_boundary_guard.sh --scan <dir>
#       Scan an arbitrary directory (used by guard self-tests).

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

SCAN_ROOT="demos"
SCAN_GLOB="Aevatar.Demos.Inspector*"

if [[ "${1:-}" == "--scan" ]] && [[ -n "${2:-}" ]]; then
  SCAN_ROOT="$2"
  SCAN_GLOB="*"
fi

# If no Inspector directory exists, the guard is a scaffold no-op.
shopt -s nullglob
inspector_dirs=( "${SCAN_ROOT}"/${SCAN_GLOB} )
shopt -u nullglob

if [[ ${#inspector_dirs[@]} -eq 0 ]]; then
  echo "Inspector tier boundary guard: no Inspector code found at ${SCAN_ROOT}/${SCAN_GLOB} (skipping — Phase B not yet landed)."
  exit 0
fi

# Sanity: at least one .cs file should exist before we bother scanning.
cs_present=$(rg --files -g '*.cs' "${inspector_dirs[@]}" 2>/dev/null | head -n 1 || true)
if [[ -z "${cs_present}" ]]; then
  echo "Inspector tier boundary guard: no .cs files under ${SCAN_ROOT}/${SCAN_GLOB} (skipping)."
  exit 0
fi

violations=0

# Violation 1 — Tier 1 endpoint touching the Tier 2 telemetry channel.
#
# A controller file that has BOTH:
#   (a) a [Http*(...)] attribute pointing at /api/inspector/<not "events">, AND
#   (b) a reference to TelemetryFrame / Channel<...Frame> / telemetry channel
# is a Tier 1 endpoint reading Tier 2 state — boundary violation.
#
# Minimal API endpoints are checked per `Map*` block so a route file can host
# both Tier 1 query endpoints and the single `/api/inspector/events` Tier 2
# stream without a false positive.
endpoint_files=$(rg -l -g '*.cs' -P '\[Http(Get|Post|Put|Delete|Patch)\("/api/inspector/(?!events\b)[^"]+"' \
  "${inspector_dirs[@]}" 2>/dev/null || true)

if [[ -n "${endpoint_files}" ]]; then
  while IFS= read -r f; do
    if rg -qP '\bTelemetryFrame\b|Channel<\s*\w*Frame\s*>|\b_telemetryChannel\b|\bTelemetryChannel\b' "$f" 2>/dev/null; then
      echo "${f}: Tier 1 endpoint references TelemetryFrame / telemetry channel — Tier 1/Tier 2 boundary violation (ADR 0022)"
      violations=$((violations + 1))
    fi
  done <<<"${endpoint_files}"
fi

minimal_endpoint_hits=$(rg -n -g '*.cs' -P '\bMap(Get|Post|Put|Delete|Patch)\s*\(\s*"/api/inspector/(?!events\b)[^"]+"' \
  "${inspector_dirs[@]}" 2>/dev/null || true)

if [[ -n "${minimal_endpoint_hits}" ]]; then
  while IFS=: read -r f line _; do
    endpoint_block=$(sed -n "${line},$((line + 120))p" "$f" | awk '
      NR == 1 {
        print
        next
      }
      {
        print
      }
      /^[[:space:]]*\}\);[[:space:]]*$/ || /^[[:space:]]*\);[[:space:]]*$/ || /^[[:space:]]*.*\)\);[[:space:]]*$/ {
        exit
      }
    ')

    if printf '%s\n' "${endpoint_block}" | rg -qP '\bTelemetryFrame\b|\bInspectorTelemetryBroadcaster\b|Channel<\s*\w*Frame\s*>|\b_telemetryChannel\b|\bTelemetryChannel\b' 2>/dev/null; then
      echo "${f}:${line}: Tier 1 minimal API endpoint references TelemetryFrame / telemetry channel — Tier 1/Tier 2 boundary violation (ADR 0022)"
      violations=$((violations + 1))
    fi
  done <<<"${minimal_endpoint_hits}"
fi

# Violation 2 — collection-shaped TelemetryFrame return.
#
# IAsyncEnumerable<TelemetryFrame> and ChannelReader<TelemetryFrame> are
# legitimate Tier 2 streaming shapes — NOT matched here.
collection_pattern='\b(IEnumerable|IReadOnlyList|IReadOnlyCollection|IList|List|ICollection)<\s*TelemetryFrame\s*>'
collection_hits=$(rg -n -g '*.cs' -P "${collection_pattern}" "${inspector_dirs[@]}" 2>/dev/null || true)
if [[ -n "${collection_hits}" ]]; then
  while IFS= read -r line; do
    echo "${line}: collection-typed TelemetryFrame return — Tier 2 must not serve historical queries (ADR 0022)"
    violations=$((violations + 1))
  done <<<"${collection_hits}"
fi

array_pattern='\bTelemetryFrame\s*\[\s*\]'
array_hits=$(rg -n -g '*.cs' -P "${array_pattern}" "${inspector_dirs[@]}" 2>/dev/null || true)
if [[ -n "${array_hits}" ]]; then
  while IFS= read -r line; do
    echo "${line}: TelemetryFrame[] return — Tier 2 must not serve historical queries (ADR 0022)"
    violations=$((violations + 1))
  done <<<"${array_hits}"
fi

if [[ ${violations} -gt 0 ]]; then
  echo
  echo "Inspector tier boundary guard FAILED — ${violations} violation(s). See ADR 0023 (Two-tier Inspector architecture)."
  exit 1
fi

echo "Inspector tier boundary guard passed."
