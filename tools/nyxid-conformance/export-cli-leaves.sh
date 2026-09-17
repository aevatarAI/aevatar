#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <nyxid-source-root> <output-json>" >&2
  exit 2
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
NYXID_SOURCE_ROOT="$(cd -- "$1" && pwd)"
OUTPUT_JSON="$2"
CLI_SOURCE="${NYXID_SOURCE_ROOT}/cli/src/cli.rs"

if [[ ! -f "${CLI_SOURCE}" ]]; then
  echo "NyxID CLI source not found: ${CLI_SOURCE}" >&2
  exit 1
fi

TEMP_DIR="$(mktemp -d)"
cleanup() {
  rm -rf -- "${TEMP_DIR}"
}
trap cleanup EXIT

mkdir -p "${TEMP_DIR}/src"
cp "${SCRIPT_DIR}/clap-exporter/Cargo.toml" "${TEMP_DIR}/Cargo.toml"
cp "${SCRIPT_DIR}/clap-exporter/Cargo.lock" "${TEMP_DIR}/Cargo.lock"
cp "${SCRIPT_DIR}/clap-exporter/src/main.rs" "${TEMP_DIR}/src/main.rs"
cp "${CLI_SOURCE}" "${TEMP_DIR}/src/cli.rs"

CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-${TEMP_DIR}/target}" \
  cargo run --quiet --locked --manifest-path "${TEMP_DIR}/Cargo.toml" > "${OUTPUT_JSON}"
