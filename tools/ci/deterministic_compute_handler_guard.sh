#!/usr/bin/env bash
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

implementation_lines="$(
  rg -n \
    '^\s*(public|internal)\s+(sealed\s+)?class\s+[A-Za-z0-9_]+\s*:\s*[^\n]*\bIDeterministicComputeHandler\b' \
    src agents \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' || true
)"

if [ -z "${implementation_lines}" ]; then
  echo "Deterministic compute handler guard failed: at least one production IDeterministicComputeHandler is required."
  exit 1
fi

violations=""
while IFS= read -r implementation_line; do
  [ -z "${implementation_line}" ] && continue
  file="${implementation_line%%:*}"
  remainder="${implementation_line#*:}"
  line_no="${remainder%%:*}"
  declaration="${remainder#*:}"
  class_name="$(echo "${declaration}" | sed -E 's/.*class[[:space:]]+([A-Za-z0-9_]+).*/\1/')"

  if [ -z "${class_name}" ] || ! rg -n "\b${class_name}\b" test -g '*Tests.cs' >/dev/null; then
    violations="${violations}${file}:${line_no}:${class_name}\n"
  fi
done <<< "${implementation_lines}"

if [ -n "${violations}" ]; then
  printf '%b' "${violations}"
  echo "Deterministic compute handler guard failed: every registered handler must be referenced by a golden-vector test."
  exit 1
fi

echo "Deterministic compute handler guard passed."
