#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${ROOT_DIR}"

CLI_FRONTEND_DIR="tools/Aevatar.Tools.Cli/Frontend"
DEMO_PLAYGROUND_DIR="demos/Aevatar.Demos.Workflow.Web/wwwroot"

failures=0

normalize_css() {
  local source_file="$1"
  local output_file="$2"
  awk '
    BEGIN { skip = 0 }
    /^[[:space:]]*\.sidebar-tools[[:space:]]*\{/ { skip = 1; next }
    /^[[:space:]]*\.sidebar-tool-btn[[:space:]]*\{/ { skip = 1; next }
    skip == 1 {
      if ($0 ~ /^[[:space:]]*}[[:space:]]*$/) {
        skip = 0;
      }
      next;
    }
    {
      if ($0 ~ /[^[:space:]]/) {
        print;
      }
    }
  ' "${source_file}" > "${output_file}"
}

normalize_index_html() {
  local source_file="$1"
  local output_file="$2"
  awk '
    BEGIN { skip = 0 }
    /<div class="sidebar-tools">/ { skip = 1; next }
    skip == 1 {
      if ($0 ~ /<\/div>/) {
        skip = 0;
      }
      next;
    }
    {
      if ($0 ~ /[^[:space:]]/) {
        print;
      }
    }
  ' "${source_file}" > "${output_file}"
}

compare_files() {
  local label="$1"
  local left_file="$2"
  local right_file="$3"

  if ! cmp -s "${left_file}" "${right_file}"; then
    echo "Playground asset drift detected for ${label}:"
    diff -u "${left_file}" "${right_file}" || true
    failures=1
  fi
}

tmp_dir="$(mktemp -d)"
trap 'rm -rf "${tmp_dir}"' EXIT

CLI_PLAYGROUND_DIR="${tmp_dir}/cli-playground"

if ! command -v pnpm &>/dev/null; then
  echo "Playground asset drift guard: pnpm not found, skipping (install pnpm to enable this guard)"
  exit 0
fi

echo "Building CLI playground assets into temporary directory..."
pnpm -C "${CLI_FRONTEND_DIR}" exec tsc -b >/dev/null
pnpm -C "${CLI_FRONTEND_DIR}" exec vite build --outDir "${CLI_PLAYGROUND_DIR}" >/dev/null

compare_files \
  "app.js" \
  "${CLI_PLAYGROUND_DIR}/app.js" \
  "${DEMO_PLAYGROUND_DIR}/app.js"

normalize_css "${CLI_PLAYGROUND_DIR}/app.css" "${tmp_dir}/cli-app.css"
normalize_css "${DEMO_PLAYGROUND_DIR}/app.css" "${tmp_dir}/demo-app.css"
compare_files "app.css (normalized)" "${tmp_dir}/cli-app.css" "${tmp_dir}/demo-app.css"

normalize_index_html "${CLI_PLAYGROUND_DIR}/index.html" "${tmp_dir}/cli-index.html"
normalize_index_html "${DEMO_PLAYGROUND_DIR}/index.html" "${tmp_dir}/demo-index.html"
compare_files "index.html (normalized)" "${tmp_dir}/cli-index.html" "${tmp_dir}/demo-index.html"

if [[ "${failures}" -ne 0 ]]; then
  echo "Playground asset drift guard failed."
  exit 1
fi

echo "Playground asset drift guard passed."
