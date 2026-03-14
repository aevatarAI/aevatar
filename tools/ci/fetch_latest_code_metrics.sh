#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ARTIFACT_NAME="code-metrics-reports"
WORKFLOW_REF="ci.yml"
TARGET_DIR="${REPO_ROOT}/artifacts/code-metrics/latest"
RUN_ID=""
BRANCH=""
DRY_RUN=0

usage() {
  cat <<'EOF'
Usage:
  bash tools/ci/fetch_latest_code_metrics.sh [options]

Options:
  --run-id <id>         Download a specific GitHub Actions run.
  --branch <name>       Limit candidate runs to a branch.
  --workflow <ref>      Workflow file or workflow name. Default: ci.yml
  --artifact <name>     Artifact name. Default: code-metrics-reports
  --target <dir>        Extraction target. Default: artifacts/code-metrics/latest
  --dry-run             Print candidate runs without downloading artifacts.
  -h, --help            Show this help text.

Examples:
  bash tools/ci/fetch_latest_code_metrics.sh
  bash tools/ci/fetch_latest_code_metrics.sh --branch main
  bash tools/ci/fetch_latest_code_metrics.sh --run-id 23096882643
EOF
}

while (($# > 0)); do
  case "$1" in
    --run-id)
      RUN_ID="${2:-}"
      shift 2
      ;;
    --branch)
      BRANCH="${2:-}"
      shift 2
      ;;
    --workflow)
      WORKFLOW_REF="${2:-}"
      shift 2
      ;;
    --artifact)
      ARTIFACT_NAME="${2:-}"
      shift 2
      ;;
    --target)
      TARGET_DIR="${2:-}"
      shift 2
      ;;
    --dry-run)
      DRY_RUN=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if ! command -v gh >/dev/null 2>&1; then
  echo "GitHub CLI 'gh' is required." >&2
  exit 1
fi

gh auth status >/dev/null

REPO_SLUG="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"

resolve_candidates() {
  if [[ -n "${RUN_ID}" ]]; then
    gh run view "${RUN_ID}" \
      --json databaseId,headBranch,status,conclusion,createdAt,displayTitle,url \
      --jq 'select(.status == "completed" and .conclusion == "success") | [.databaseId, .headBranch, .createdAt, .displayTitle, .url] | @tsv'
    return
  fi

  local branch_args=()
  if [[ -n "${BRANCH}" ]]; then
    branch_args+=(--branch "${BRANCH}")
  fi

  gh run list \
    --workflow "${WORKFLOW_REF}" \
    --limit 50 \
    "${branch_args[@]}" \
    --json databaseId,headBranch,status,conclusion,createdAt,displayTitle,url \
    --jq '.[] | select(.status == "completed" and .conclusion == "success") | [.databaseId, .headBranch, .createdAt, .displayTitle, .url] | @tsv'
}

CANDIDATES=()
while IFS= read -r candidate; do
  [[ -n "${candidate}" ]] || continue
  CANDIDATES+=("${candidate}")
done < <(resolve_candidates)

if [[ "${#CANDIDATES[@]}" -eq 0 ]]; then
  echo "No successful completed runs found for workflow '${WORKFLOW_REF}'." >&2
  exit 1
fi

if [[ "${DRY_RUN}" -eq 1 ]]; then
  printf 'Repo: %s\nWorkflow: %s\nArtifact: %s\nTarget: %s\n' \
    "${REPO_SLUG}" "${WORKFLOW_REF}" "${ARTIFACT_NAME}" "${TARGET_DIR}"
  printf 'Candidate runs:\n'
  for candidate in "${CANDIDATES[@]}"; do
    IFS=$'\t' read -r candidate_run_id candidate_branch candidate_created_at candidate_title candidate_url <<<"${candidate}"
    printf '  - run_id=%s branch=%s created_at=%s title=%s url=%s\n' \
      "${candidate_run_id}" "${candidate_branch}" "${candidate_created_at}" "${candidate_title}" "${candidate_url}"
  done
  exit 0
fi

DOWNLOAD_DIR="$(mktemp -d)"
trap 'rm -rf "${DOWNLOAD_DIR}"' EXIT

SELECTED_RUN_ID=""
SELECTED_BRANCH=""
SELECTED_CREATED_AT=""
SELECTED_TITLE=""
SELECTED_URL=""

for candidate in "${CANDIDATES[@]}"; do
  IFS=$'\t' read -r candidate_run_id candidate_branch candidate_created_at candidate_title candidate_url <<<"${candidate}"
  ERROR_FILE="$(mktemp)"
  if gh run download "${candidate_run_id}" -n "${ARTIFACT_NAME}" -D "${DOWNLOAD_DIR}" > /dev/null 2>"${ERROR_FILE}"; then
    SELECTED_RUN_ID="${candidate_run_id}"
    SELECTED_BRANCH="${candidate_branch}"
    SELECTED_CREATED_AT="${candidate_created_at}"
    SELECTED_TITLE="${candidate_title}"
    SELECTED_URL="${candidate_url}"
    rm -f "${ERROR_FILE}"
    break
  fi

  if grep -q "no valid artifacts found to download" "${ERROR_FILE}"; then
    rm -f "${ERROR_FILE}"
    continue
  fi

  cat "${ERROR_FILE}" >&2
  rm -f "${ERROR_FILE}"
  exit 1
done

if [[ -z "${SELECTED_RUN_ID}" ]]; then
  echo "No '${ARTIFACT_NAME}' artifact was found in the latest successful runs for workflow '${WORKFLOW_REF}'." >&2
  exit 1
fi

MANIFEST_PATH="$(find "${DOWNLOAD_DIR}" -name manifest.json -print | head -n 1)"
if [[ -z "${MANIFEST_PATH}" ]]; then
  echo "Downloaded artifact does not contain manifest.json." >&2
  exit 1
fi

REPORT_ROOT="$(cd -- "$(dirname -- "${MANIFEST_PATH}")" && pwd)"
rm -rf "${TARGET_DIR}"
mkdir -p "${TARGET_DIR}"
cp -R "${REPORT_ROOT}/." "${TARGET_DIR}/"

cat > "${TARGET_DIR}/source-run.json" <<EOF
{
  "repository": "${REPO_SLUG}",
  "workflow": "${WORKFLOW_REF}",
  "artifact": "${ARTIFACT_NAME}",
  "runId": ${SELECTED_RUN_ID},
  "branch": "${SELECTED_BRANCH}",
  "createdAt": "${SELECTED_CREATED_AT}",
  "title": "${SELECTED_TITLE}",
  "url": "${SELECTED_URL}"
}
EOF

printf 'Downloaded %s from run %s into %s\n' \
  "${ARTIFACT_NAME}" "${SELECTED_RUN_ID}" "${TARGET_DIR}"
