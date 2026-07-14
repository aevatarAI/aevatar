#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

usage() {
  cat <<'USAGE'
Usage:
  scripts/run.sh main-flow-smoke
  scripts/run.sh test [github-devloop|devloop]
  scripts/run.sh test-affected

Commands:
  main-flow-smoke   Run main product flow smoke against a single-node Mainnet host.
  test              Run scoped FKST package tests for local package overrides.
  test-affected     Run tests derived from uncommitted changed paths.

Environment:
  AEVATAR_MAIN_FLOW_SMOKE_HTTP_PORT=18082    Override main-flow smoke HTTP port.
  AEVATAR_MAIN_FLOW_SMOKE_SILO_PORT=11112    Override main-flow smoke Orleans silo port.
  AEVATAR_MAIN_FLOW_SMOKE_GATEWAY_PORT=30001 Override main-flow smoke Orleans gateway port.
  BIN                                           Override fkst-framework binary.
USAGE
}

resolve_fkst_bin() {
  if [ -n "${BIN:-}" ]; then
    if [ -x "$BIN" ]; then
      printf '%s\n' "$BIN"
      return 0
    fi
    echo "error: BIN is set but is not executable: $BIN" >&2
    return 1
  fi

  local candidate
  for candidate in \
    "/Users/liyingpei/Desktop/Code/fkst-substrate/target/debug/fkst-framework" \
    "/Users/liyingpei/.cache/fkst/fkst-substrate-bin/v1/ChronoAIProject/fkst-substrate/723f5433814975d5c02723e7c5874190c7b8ed73/target/debug/fkst-framework"
  do
    if [ -x "$candidate" ]; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done

  if command -v fkst-framework >/dev/null 2>&1; then
    command -v fkst-framework
    return 0
  fi

  echo "error: fkst-framework BIN is unreachable. Set BIN to an executable fkst-framework." >&2
  return 1
}

copy_local_package() {
  local source="$1"
  local dest="$2"
  shift 2

  mkdir -p "$dest"
  (
    cd "$source"
    tar "$@" -cf - .
  ) | (
    cd "$dest"
    tar xf -
  )
}

run_github_devloop_test() {
  local framework_bin work pkg runtime durable report
  framework_bin="$(resolve_fkst_bin)"

  work="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-fkst-test.XXXXXX")"
  runtime="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-fkst-rt.XXXXXX")"
  durable="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-fkst-durable.XXXXXX")"
  trap 'rm -rf "${work:-}" "${runtime:-}" "${durable:-}"' RETURN

  pkg="$work/github-devloop"
  copy_local_package "${REPO_ROOT}/.fkst/local-packages/github-devloop" "$pkg" --exclude './tests/run_graph*_test.lua'

  mkdir -p "$pkg/packages" "$pkg/libraries"
  ln -s .. "$pkg/packages/github-devloop"
  for library in contract workflow testkit forge devloop; do
    copy_local_package "${REPO_ROOT}/.fkst/local-packages/${library}" "$pkg/libraries/$library"
  done

  cat > "$pkg/fkst.workspace.toml" <<'TOML'
[workspace]
units = [".", "libraries/*"]
packages = ["."]
libraries = ["libraries/*"]

[registries]
workspace = "workspace"
TOML

  report="$work/github-devloop-test-report.json"
  echo "test hermetic: FKST_RUNTIME_ROOT=$runtime FKST_DURABLE_ROOT=$durable"
  (
    cd "$pkg"
    FKST_RUNTIME_ROOT="$runtime" \
    FKST_DURABLE_ROOT="$durable" \
    "$framework_bin" test --project-root "$pkg" --package-root "$pkg" --report-json "$report"
  )
}

cmd_test() {
  local target="${1:-github-devloop}"
  if [ "$#" -gt 1 ]; then
    echo "usage: scripts/run.sh test [github-devloop|devloop]" >&2
    exit 2
  fi

  case "$target" in
    github-devloop|devloop|"")
      run_github_devloop_test
      ;;
    *)
      echo "error: unsupported local FKST test target: $target" >&2
      exit 2
      ;;
  esac
}

changed_paths() {
  {
    git -C "$REPO_ROOT" diff --name-only HEAD
    git -C "$REPO_ROOT" ls-files --others --exclude-standard
  } | sed '/^$/d' | LC_ALL=C sort -u
}

cmd_test_affected() {
  local path should_run=0
  while IFS= read -r path || [ -n "$path" ]; do
    case "$path" in
      .fkst/local-packages/*|fkst.workspace.toml|scripts/run.sh)
        should_run=1
        ;;
    esac
  done < <(changed_paths)

  if [ "$should_run" -eq 1 ]; then
    cmd_test github-devloop
    return $?
  fi

  cmd_test github-devloop
}

case "${1:-}" in
  main-flow-smoke)
    shift
    exec bash "${REPO_ROOT}/tools/ci/main_flow_runtime_smoke.sh" "$@"
    ;;
  test)
    shift
    cmd_test "$@"
    ;;
  test-affected)
    shift
    cmd_test_affected "$@"
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
