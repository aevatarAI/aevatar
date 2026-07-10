#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
SOURCE_GUARD="${REPO_ROOT}/tools/ci/fkst_host_policy_guard.sh"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

export GITHUB_REPOSITORY="aevatarAI/aevatar"
export GITHUB_EVENT_NAME="pull_request"
export PR_NUMBER="2631"

run_guard() {
  local worktree="$1"
  local output="$2"

  (
    cd "${worktree}"
    GITHUB_BASE_SHA="$(git rev-parse HEAD~1)" \
      GITHUB_HEAD_SHA="$(git rev-parse HEAD)" \
      PATH="${TMP_DIR}/bin:${PATH}" \
      bash tools/ci/fkst_host_policy_guard.sh
  ) > "${output}" 2>&1
}

create_worktree() {
  local name="$1"
  local path="${TMP_DIR}/${name}"

  git init -q "${path}"
  (
    cd "${path}"
    git config user.email "ci@example.invalid"
    git config user.name "CI Test"
    mkdir -p apps/aevatar-console-web/src src/Aevatar.Foundation.Core tools/ci
    cp "${SOURCE_GUARD}" tools/ci/fkst_host_policy_guard.sh
    printf 'initial\n' > README.md
    git add README.md tools/ci/fkst_host_policy_guard.sh
    git commit -q -m "initial"
  )

  printf '%s\n' "${path}"
}

install_mock_gh() {
  mkdir -p "${TMP_DIR}/bin"
  cat > "${TMP_DIR}/bin/gh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf 'gh was called\n' >> "${GH_CALL_LOG:?}"
cat <<'JSON'
{
  "data": {
    "repository": {
      "pullRequest": {
        "reviewThreads": {
          "pageInfo": { "hasNextPage": false, "endCursor": null },
          "nodes": []
        },
        "comments": {
          "pageInfo": { "hasNextPage": false, "endCursor": null },
          "nodes": []
        }
      }
    }
  }
}
JSON
SH
  chmod +x "${TMP_DIR}/bin/gh"
}

install_mock_gh

frontend_worktree="$(create_worktree frontend-only)"
(
  cd "${frontend_worktree}"
  printf 'frontend\n' > apps/aevatar-console-web/src/App.tsx
  git add apps/aevatar-console-web/src/App.tsx
  git commit -q -m "frontend"
)
frontend_output="${TMP_DIR}/frontend.out"
frontend_gh_log="${TMP_DIR}/frontend-gh.log"
export GH_CALL_LOG="${frontend_gh_log}"
run_guard "${frontend_worktree}" "${frontend_output}"

if ! grep -Fq "No backend-impact paths changed; skipped unresolved P1/P2 review-comment gate." "${frontend_output}"; then
  echo "Expected frontend-only changes to skip FKST host policy review scan."
  cat "${frontend_output}"
  exit 1
fi

if [[ -s "${frontend_gh_log}" ]]; then
  echo "Expected frontend-only changes not to query GitHub review threads."
  cat "${frontend_output}"
  cat "${frontend_gh_log}"
  exit 1
fi

backend_worktree="$(create_worktree backend-change)"
(
  cd "${backend_worktree}"
  printf 'backend\n' > src/Aevatar.Foundation.Core/Backend.cs
  git add src/Aevatar.Foundation.Core/Backend.cs
  git commit -q -m "backend"
)
backend_output="${TMP_DIR}/backend.out"
backend_gh_log="${TMP_DIR}/backend-gh.log"
export GH_CALL_LOG="${backend_gh_log}"
run_guard "${backend_worktree}" "${backend_output}"

if ! grep -Fq "Backend-impact paths changed:" "${backend_output}"; then
  echo "Expected backend changes to enter FKST host policy review scan."
  cat "${backend_output}"
  exit 1
fi

if ! grep -Fq "No unresolved P1/P2 review comments found." "${backend_output}"; then
  echo "Expected backend scan to complete when no P1/P2 blockers exist."
  cat "${backend_output}"
  exit 1
fi

if [[ ! -s "${backend_gh_log}" ]]; then
  echo "Expected backend changes to query GitHub review threads."
  cat "${backend_output}"
  exit 1
fi

echo "fkst host policy guard tests passed"
