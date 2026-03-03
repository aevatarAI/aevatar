#!/usr/bin/env bash

set -euo pipefail

TOOL_PACKAGE_ID="aevatar"
CONFIGURATION="${1:-Release}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT_PATH="${REPO_ROOT}/tools/Aevatar.Tools.Cli/Aevatar.Tools.Cli.csproj"
PACKAGE_SOURCE_DIR="${REPO_ROOT}/tools/Aevatar.Tools.Cli/bin/${CONFIGURATION}"

echo "==> Tool package: ${TOOL_PACKAGE_ID}"
echo "==> Build configuration: ${CONFIGURATION}"
echo "==> Project: ${PROJECT_PATH}"

if [[ ! -f "${PROJECT_PATH}" ]]; then
  echo "Project file not found: ${PROJECT_PATH}" >&2
  exit 1
fi

if dotnet tool list --global | awk -v tool="${TOOL_PACKAGE_ID}" '
  NR > 2 && $1 == tool { found = 1 }
  END { exit(found ? 0 : 1) }
'; then
  echo "==> Found existing global tool. Uninstalling ${TOOL_PACKAGE_ID}..."
  dotnet tool uninstall --global "${TOOL_PACKAGE_ID}"
else
  echo "==> Global tool ${TOOL_PACKAGE_ID} is not installed. Skipping uninstall."
fi

echo "==> Packing tool..."
dotnet pack "${PROJECT_PATH}" -c "${CONFIGURATION}"

if [[ ! -d "${PACKAGE_SOURCE_DIR}" ]]; then
  echo "Package source directory not found: ${PACKAGE_SOURCE_DIR}" >&2
  exit 1
fi

echo "==> Installing tool from local source only: ${PACKAGE_SOURCE_DIR}"
TMP_NUGET_CONFIG="$(mktemp)"
trap 'rm -f "${TMP_NUGET_CONFIG}"' EXIT
cat > "${TMP_NUGET_CONFIG}" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="${PACKAGE_SOURCE_DIR}" />
  </packageSources>
</configuration>
EOF

dotnet tool install --global --configfile "${TMP_NUGET_CONFIG}" --no-cache "${TOOL_PACKAGE_ID}"

echo "==> Done. You can run:"
echo "    ${TOOL_PACKAGE_ID}"
