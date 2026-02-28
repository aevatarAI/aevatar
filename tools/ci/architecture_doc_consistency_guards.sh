#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

impl_doc="docs/architecture/ai-script-runtime-implementation-change-plan.md"
req_doc="docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md"

for file in "${impl_doc}" "${req_doc}"; do
  if [[ ! -f "${file}" ]]; then
    echo "Missing required architecture document: ${file}"
    exit 1
  fi
done

for file in "${impl_doc}" "${req_doc}"; do
  if ! rg -q "Adapter-only" "${file}"; then
    echo "Adapter-only stance must be declared in ${file}."
    exit 1
  fi
done

risky_pattern="Native[[:space:]]*\\+[[:space:]]*Adapter|Native 模式|双模式|dual[-[:space:]]mode"
deprecated_context="废弃|禁止|不再|淘汰|不得|失败|禁用|deprecated|forbidden"

violations=""
while IFS= read -r hit; do
  [[ -z "${hit}" ]] && continue

  line_text="${hit#*:*:}"
  if ! printf '%s\n' "${line_text}" | rg -qi "${deprecated_context}"; then
    violations="${violations}${hit}"$'\n'
  fi
done <<< "$(rg -n "${risky_pattern}" "${impl_doc}" "${req_doc}" || true)"

if [[ -n "${violations}" ]]; then
  echo "Detected ambiguous dual-mode wording without deprecation context:"
  printf '%s' "${violations}"
  echo "Use Adapter-only wording or explicitly mark legacy terms as deprecated/forbidden."
  exit 1
fi

host_optional_pattern="Script\\.Host\\.Api.*可选|可选.*Script\\.Host\\.Api|独立\\s*Host.*可选|可选.*独立\\s*Host"
host_violations=""
while IFS= read -r hit; do
  [[ -z "${hit}" ]] && continue

  line_text="${hit#*:*:}"
  if ! printf '%s\n' "${line_text}" | rg -qi "${deprecated_context}"; then
    host_violations="${host_violations}${hit}"$'\n'
  fi
done <<< "$(rg -n "${host_optional_pattern}" "${impl_doc}" "${req_doc}" || true)"

if [[ -n "${host_violations}" ]]; then
  echo "Host strategy must not stay optional in architecture documents:"
  printf '%s' "${host_violations}"
  exit 1
fi

echo "Architecture document consistency guard passed."
