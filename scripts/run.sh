#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

usage() {
  cat <<'USAGE'
Usage:
  scripts/run.sh test [github-devloop-ops]
  scripts/run.sh test-affected
  scripts/run.sh main-flow-smoke

Commands:
  test              Run a local FKST package test suite.
  test-affected     Run tests selected from uncommitted changed paths.
  main-flow-smoke   Run main product flow smoke against a single-node Mainnet host.

Environment:
  BIN                                           Override fkst-framework binary.
  AEVATAR_MAIN_FLOW_SMOKE_HTTP_PORT=18082    Override main-flow smoke HTTP port.
  AEVATAR_MAIN_FLOW_SMOKE_SILO_PORT=11112    Override main-flow smoke Orleans silo port.
  AEVATAR_MAIN_FLOW_SMOKE_GATEWAY_PORT=30001 Override main-flow smoke Orleans gateway port.
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
  candidate="$(find "$HOME/.cache/fkst/fkst-substrate-bin" -type f -perm -111 -name fkst-framework 2>/dev/null | LC_ALL=C sort | tail -n 1)"
  if [ -n "$candidate" ] && [ -x "$candidate" ]; then
    printf '%s\n' "$candidate"
    return 0
  fi
  if command -v fkst-framework >/dev/null 2>&1; then
    command -v fkst-framework
    return 0
  fi

  echo "error: fkst-framework BIN is unreachable. Set BIN to an executable fkst-framework." >&2
  return 1
}

run_fkst_package_test() {
  local package_name="$1"
  local framework_bin work_root package_root runtime_root durable_root test_status
  framework_bin="$(resolve_fkst_bin)"
  work_root="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-fkst-test.XXXXXX")"
  package_root="$work_root/$package_name"
  runtime_root="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-fkst-rt.XXXXXX")"
  durable_root="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-fkst-durable.XXXXXX")"

  mkdir -p "$package_root" "$package_root/libraries" "$package_root/packages"
  (
    cd "$REPO_ROOT/.fkst/local-packages/$package_name"
    tar -cf - .
  ) | (
    cd "$package_root"
    tar xf -
  )
  ln -s .. "$package_root/packages/$package_name"
  local library
  for library in contract forge testkit workflow devloop; do
    mkdir -p "$package_root/libraries/$library"
    (
      cd "$REPO_ROOT/.fkst/local-packages/$library"
      tar -cf - .
    ) | (
      cd "$package_root/libraries/$library"
      tar xf -
    )
  done
  cat > "$package_root/fkst.workspace.toml" <<'TOML'
[workspace]
units = [".", "libraries/*"]
packages = ["."]
libraries = ["libraries/*"]

[registries]
workspace = "workspace"
TOML

  if (
    cd "$package_root"
    BIN="$framework_bin" \
    FKST_RUNTIME_ROOT="$runtime_root" \
    FKST_DURABLE_ROOT="$durable_root" \
      "$framework_bin" test \
        --project-root "$package_root" \
        --package-root "$package_root"
  ); then
    test_status=0
  else
    test_status=$?
  fi
  rm -rf "$work_root" "$runtime_root" "$durable_root"
  return "$test_status"
}

changed_paths() {
  {
    git -C "$REPO_ROOT" diff --name-only HEAD
    git -C "$REPO_ROOT" ls-files --others --exclude-standard
  } | sed '/^$/d' | LC_ALL=C sort -u
}

test_affected() {
  local path package_name=""
  while IFS= read -r path || [ -n "$path" ]; do
    case "$path" in
      .fkst/local-packages/github-devloop-ops/*|.fkst/local-packages/contract/*|.fkst/local-packages/forge/*|.fkst/local-packages/testkit/*|.fkst/local-packages/workflow/*|.fkst/local-packages/devloop/*|fkst.workspace.toml|fkst.lock|scripts/run.sh)
        package_name="github-devloop-ops"
        ;;
      *)
        echo "error: changed path requires the full repository test suite: $path" >&2
        return 2
        ;;
    esac
  done < <(changed_paths)

  if [ -z "$package_name" ]; then
    echo "error: no affected package found" >&2
    return 2
  fi
  run_fkst_package_test "$package_name"
}

case "${1:-}" in
  test)
    shift
    case "${1:-github-devloop-ops}" in
      github-devloop-ops)
        run_fkst_package_test "${1:-github-devloop-ops}"
        ;;
      *)
        echo "error: unsupported local FKST test target: ${1:-}" >&2
        exit 2
        ;;
    esac
    ;;
  test-affected)
    shift
    test_affected
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
