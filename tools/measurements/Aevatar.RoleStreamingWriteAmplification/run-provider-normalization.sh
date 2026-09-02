#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
project="$repo_root/tools/measurements/Aevatar.RoleStreamingWriteAmplification/Aevatar.RoleStreamingWriteAmplification.csproj"
config="$repo_root/tools/measurements/Aevatar.RoleStreamingWriteAmplification/provider-normalization.config.json"
output="${1:-$repo_root/docs/audit-scorecard/raw/2026-08-02-role-provider-normalization.json}"

dotnet run --project "$project" --configuration Release -- \
  --measurement provider-normalization \
  --config "$config" \
  --output "$output"

output_directory="$(cd "$(dirname "$output")" && pwd)"
output_name="$(basename "$output")"
(
  cd "$output_directory"
  shasum -a 256 "$output_name" > "$output_name.sha256"
)
