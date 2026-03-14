#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

TARGETS=(
  "test/Aevatar.Scripting.Core.Tests/Protos"
  "test/Aevatar.Integration.Tests/Protos"
  "test/Aevatar.Scripting.Core.Tests/ScriptSources.cs"
  "test/Aevatar.Scripting.Core.Tests/ClaimScriptSources.cs"
  "test/Aevatar.Integration.Tests/ScriptEvolutionIntegrationSources.cs"
)

if rg -n "wrappers\\.proto|google\\.protobuf\\.(StringValue|BoolValue|Int32Value|Int64Value|UInt32Value|UInt64Value|DoubleValue|FloatValue|BytesValue)" "${TARGETS[@]}"; then
  echo "Scripting read model fixtures must not use protobuf wrapper leaf types. Use scalar fields, proto3 optional fields, or typed sub-messages."
  exit 1
fi

echo "Scripting read model wrapper guard passed."
