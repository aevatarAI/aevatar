#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

if rg -n 'ProjectReference Include=".*platforms[\\/].*\.csproj"' agents/channels -g '*.csproj'; then
  echo "Channel projects must not ProjectReference platform projects directly."
  exit 1
fi
