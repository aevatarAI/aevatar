#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
project="$repo_root/tools/measurements/Aevatar.RoleStreamingWriteAmplification/Aevatar.RoleStreamingWriteAmplification.csproj"
config="$repo_root/tools/measurements/Aevatar.RoleStreamingWriteAmplification/role-contention.config.json"
run_phase="${1:-baseline-pre-3135}"
output="${2:-$repo_root/docs/audit-scorecard/raw/2026-08-02-role-actor-contention-${run_phase}.json}"

dotnet run --project "$project" --configuration Release -- \
  --measurement role-contention \
  --run-phase "$run_phase" \
  --adapter inmemory \
  --config "$config" \
  --output "$output"
