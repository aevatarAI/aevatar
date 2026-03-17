#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

WORKFLOW_REF="ci.yml"
TARGET_DIR="${REPO_ROOT}/artifacts/ci-failures/latest"
RUN_ID=""
BRANCH=""
DRY_RUN=0

usage() {
  cat <<'EOF'
Usage:
  bash tools/ci/fetch_latest_ci_failure.sh [options]

Options:
  --run-id <id>         Fetch a specific failed GitHub Actions run.
  --branch <name>       Limit candidate runs to a branch. Default: current branch.
  --workflow <ref>      Workflow file or workflow name. Default: ci.yml
  --target <dir>        Extraction target. Default: artifacts/ci-failures/latest
  --dry-run             Print candidate failed runs without downloading logs.
  -h, --help            Show this help text.

Examples:
  bash tools/ci/fetch_latest_ci_failure.sh
  bash tools/ci/fetch_latest_ci_failure.sh --branch main
  bash tools/ci/fetch_latest_ci_failure.sh --run-id 23097321737
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

if [[ -z "${BRANCH}" ]]; then
  BRANCH="$(git branch --show-current)"
fi

resolve_candidates() {
  if [[ -n "${RUN_ID}" ]]; then
    gh run view "${RUN_ID}" \
      --json databaseId,headBranch,headSha,status,conclusion,createdAt,updatedAt,displayTitle,url \
      --jq 'select(.status == "completed" and .conclusion == "failure") | [.databaseId, .headBranch, .headSha, .createdAt, .displayTitle, .url] | @tsv'
    return
  fi

  gh run list \
    --workflow "${WORKFLOW_REF}" \
    --branch "${BRANCH}" \
    --limit 50 \
    --json databaseId,headBranch,headSha,status,conclusion,createdAt,displayTitle,url \
    --jq '.[] | select(.status == "completed" and .conclusion == "failure") | [.databaseId, .headBranch, .headSha, .createdAt, .displayTitle, .url] | @tsv'
}

CANDIDATES=()
while IFS= read -r candidate; do
  [[ -n "${candidate}" ]] || continue
  CANDIDATES+=("${candidate}")
done < <(resolve_candidates)

if [[ "${#CANDIDATES[@]}" -eq 0 ]]; then
  echo "No failed completed runs found for workflow '${WORKFLOW_REF}' on branch '${BRANCH}'." >&2
  exit 1
fi

if [[ "${DRY_RUN}" -eq 1 ]]; then
  printf 'Repo: %s\nWorkflow: %s\nBranch: %s\nTarget: %s\n' \
    "${REPO_SLUG}" "${WORKFLOW_REF}" "${BRANCH}" "${TARGET_DIR}"
  printf 'Candidate failed runs:\n'
  for candidate in "${CANDIDATES[@]}"; do
    IFS=$'\t' read -r candidate_run_id candidate_branch candidate_sha candidate_created_at candidate_title candidate_url <<<"${candidate}"
    printf '  - run_id=%s branch=%s sha=%s created_at=%s title=%s url=%s\n' \
      "${candidate_run_id}" "${candidate_branch}" "${candidate_sha}" "${candidate_created_at}" "${candidate_title}" "${candidate_url}"
  done
  exit 0
fi

IFS=$'\t' read -r SELECTED_RUN_ID SELECTED_BRANCH SELECTED_SHA SELECTED_CREATED_AT SELECTED_TITLE SELECTED_URL <<<"${CANDIDATES[0]}"

rm -rf "${TARGET_DIR}"
mkdir -p "${TARGET_DIR}/job-logs"

gh run view "${SELECTED_RUN_ID}" \
  --json databaseId,headBranch,headSha,status,conclusion,createdAt,updatedAt,displayTitle,url,jobs \
  > "${TARGET_DIR}/run.json"

gh run view "${SELECTED_RUN_ID}" \
  --json jobs \
  --jq '.jobs[] | select(.conclusion == "failure") | [.databaseId, .name, .url] | @tsv' \
  > "${TARGET_DIR}/failed-jobs.tsv"

gh run view "${SELECTED_RUN_ID}" --log-failed > "${TARGET_DIR}/failed.log"

while IFS=$'\t' read -r job_id job_name job_url; do
  [[ -n "${job_id}" ]] || continue
  job_slug="$(printf '%s' "${job_name}" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/-/g; s/-\{2,\}/-/g; s/^-//; s/-$//')"
  if [[ -z "${job_slug}" ]]; then
    job_slug="job-${job_id}"
  fi

  gh run view "${SELECTED_RUN_ID}" --job "${job_id}" --log-failed > "${TARGET_DIR}/job-logs/${job_id}-${job_slug}.log"
done < "${TARGET_DIR}/failed-jobs.tsv"

cat > "${TARGET_DIR}/source-run.json" <<EOF
{
  "repository": "${REPO_SLUG}",
  "workflow": "${WORKFLOW_REF}",
  "runId": ${SELECTED_RUN_ID},
  "branch": "${SELECTED_BRANCH}",
  "headSha": "${SELECTED_SHA}",
  "createdAt": "${SELECTED_CREATED_AT}",
  "title": "${SELECTED_TITLE}",
  "url": "${SELECTED_URL}"
}
EOF

printf 'Fetched failed run %s into %s\n' "${SELECTED_RUN_ID}" "${TARGET_DIR}"
