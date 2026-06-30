#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

usage() {
  cat <<'USAGE'
Usage:
  scripts/run.sh service-smoke
  scripts/run.sh main-flow-smoke

Commands:
  service-smoke     Run single-node Mainnet service smoke with in-memory Orleans and projections.
  main-flow-smoke   Run main product flow smoke against a single-node Mainnet host.

Environment:
  AEVATAR_SERVICE_SMOKE_HTTP_PORT=18081      Override service HTTP port.
  AEVATAR_SERVICE_SMOKE_SILO_PORT=11111      Override Orleans silo port.
  AEVATAR_SERVICE_SMOKE_GATEWAY_PORT=30000   Override Orleans gateway port.
  AEVATAR_MAIN_FLOW_SMOKE_HTTP_PORT=18082    Override main-flow smoke HTTP port.
  AEVATAR_MAIN_FLOW_SMOKE_SILO_PORT=11112    Override main-flow smoke Orleans silo port.
  AEVATAR_MAIN_FLOW_SMOKE_GATEWAY_PORT=30001 Override main-flow smoke Orleans gateway port.
USAGE
}

case "${1:-}" in
  service-smoke)
    shift
    exec bash "${REPO_ROOT}/tools/ci/mainnet_single_node_service_smoke.sh" "$@"
    ;;
  main-flow-smoke)
    shift
    exec bash "${REPO_ROOT}/tools/ci/main_flow_runtime_smoke.sh" "$@"
    ;;
  ""|-h|--help|help)
    usage
    ;;
  *)
    echo "Unknown command: $1" >&2
    echo "Run scripts/run.sh --help for usage." >&2
    exit 2
    ;;
esac
