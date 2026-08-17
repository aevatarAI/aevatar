#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
exec python3.12 "${SCRIPT_DIR}/nyxid_conformance_guard.py" "$@"
