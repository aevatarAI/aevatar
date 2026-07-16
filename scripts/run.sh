#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

usage() {
  cat <<'USAGE'
Usage:
  scripts/run.sh test [dotnet test arguments...]
  scripts/run.sh test-affected [dotnet test arguments...]
  scripts/run.sh main-flow-smoke

Commands:
  test              Run the complete solution test suite.
  test-affected     Conservatively run the complete solution test suite.
  main-flow-smoke   Run main product flow smoke against a single-node Mainnet host.

Environment:
  AEVATAR_MAIN_FLOW_SMOKE_HTTP_PORT=18082    Override main-flow smoke HTTP port.
  AEVATAR_MAIN_FLOW_SMOKE_SILO_PORT=11112    Override main-flow smoke Orleans silo port.
  AEVATAR_MAIN_FLOW_SMOKE_GATEWAY_PORT=30001 Override main-flow smoke Orleans gateway port.
USAGE
}

case "${1:-}" in
  test|test-affected)
    shift
    exec dotnet test "${REPO_ROOT}/aevatar.slnx" --nologo "$@"
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
