#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
project="$repo_root/tools/measurements/Aevatar.RoleStreamingWriteAmplification/Aevatar.RoleStreamingWriteAmplification.csproj"
config="$repo_root/tools/measurements/Aevatar.RoleStreamingWriteAmplification/streaming-write-amplification.config.json"
output="$repo_root/docs/audit-scorecard/raw/2026-08-02-role-streaming-write-amplification.json"

dotnet run --project "$project" --configuration Release -- \
  --config "$config" \
  --output "$output" \
  "$@"
