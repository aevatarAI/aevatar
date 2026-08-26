#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

resolve_diff_target_from_shas() {
  local base_sha="$1"
  local head_sha="$2"

  if git cat-file -e "${base_sha}^{commit}" >/dev/null 2>&1 \
    && git cat-file -e "${head_sha}^{commit}" >/dev/null 2>&1; then
    printf '%s...%s\n' "${base_sha}" "${head_sha}"
    return 0
  fi
  return 1
}

resolve_diff_target() {
  local base_ref="${1:-dev}"

  git fetch --no-tags origin \
    "+refs/heads/${base_ref}:refs/remotes/origin/${base_ref}" \
    2>/dev/null || true

  if git rev-parse --verify "origin/${base_ref}" >/dev/null 2>&1 \
    && git merge-base "origin/${base_ref}" HEAD >/dev/null 2>&1; then
    printf 'origin/%s...HEAD\n' "${base_ref}"
    return 0
  fi

  printf 'HEAD\n'
}

resolve_pr_number() {
  if [[ -n "${PR_NUMBER:-}" ]]; then
    printf '%s\n' "${PR_NUMBER}"
    return 0
  fi

  local event_path="${GITHUB_EVENT_PATH:-}"
  if [[ -n "${event_path}" && -f "${event_path}" ]]; then
    python3 - <<'PY' "${event_path}"
import json
import sys
from pathlib import Path

payload = json.loads(Path(sys.argv[1]).read_text())
number = (
    (payload.get("pull_request") or {}).get("number")
    or (payload.get("issue") or {}).get("number")
)
if number:
    print(number)
PY
  fi
}

resolve_diff_target_from_pr() {
  local pr_number="$1"
  [[ -n "${pr_number}" ]] || return 1
  [[ -n "${GITHUB_REPOSITORY:-}" ]] || return 1

  local pr_json
  pr_json="$(gh pr view "${pr_number}" --repo "${GITHUB_REPOSITORY}" --json baseRefName,baseRefOid,headRefOid 2>/dev/null)" \
    || return 1

  local base_ref base_sha head_sha
  base_ref="$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("baseRefName") or "")' <<< "${pr_json}")"
  base_sha="$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("baseRefOid") or "")' <<< "${pr_json}")"
  head_sha="$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("headRefOid") or "")' <<< "${pr_json}")"

  [[ -n "${base_sha}" && -n "${head_sha}" ]] || return 1

  git fetch --no-tags origin \
    "${base_sha}" \
    "${head_sha}" \
    "+refs/heads/${base_ref}:refs/remotes/origin/${base_ref}" \
    2>/dev/null || true

  resolve_diff_target_from_shas "${base_sha}" "${head_sha}"
}

RESOLVED_PR_NUMBER="$(resolve_pr_number || true)"
export RESOLVED_PR_NUMBER

if [[ -n "${GITHUB_BASE_SHA:-}" && -n "${GITHUB_HEAD_SHA:-}" ]]; then
  DIFF_TARGET="$(resolve_diff_target_from_shas "${GITHUB_BASE_SHA}" "${GITHUB_HEAD_SHA}")" \
    || DIFF_TARGET="$(resolve_diff_target "${GITHUB_BASE_REF:-dev}")"
elif [[ -n "${RESOLVED_PR_NUMBER}" ]]; then
  DIFF_TARGET="$(resolve_diff_target_from_pr "${RESOLVED_PR_NUMBER}")" \
    || DIFF_TARGET="$(resolve_diff_target "${GITHUB_BASE_REF:-dev}")"
elif [[ -n "${GITHUB_BASE_REF:-}" ]]; then
  DIFF_TARGET="$(resolve_diff_target "${GITHUB_BASE_REF}")"
elif [[ -n "${1:-}" ]]; then
  DIFF_TARGET="$1"
else
  DIFF_TARGET="$(resolve_diff_target dev)"
fi

echo "FKST host policy guard: comparing ${DIFF_TARGET}"

CHANGED_PATHS="$(git diff --name-only "${DIFF_TARGET}" 2>/dev/null || true)"
BACKEND_IMPACT_PATHS=""

is_backend_impact_path() {
  local changed_path="$1"

  case "${changed_path}" in
    src/*|agents/*|test/*|demos/*|scripts/*|tools/ci/*)
      return 0
      ;;
    .github/workflows/ci.yml|.github/workflows/fkst-review-policy.yml)
      return 0
      ;;
    aevatar.slnx|aevatar.*.slnf|Directory.Build.props|Directory.Packages.props|global.json|buf.work.yaml)
      return 0
      ;;
    docker-compose.yml|docker-compose.*.yml)
      return 0
      ;;
  esac

  return 1
}

while IFS= read -r changed_path; do
  [[ -n "${changed_path}" ]] || continue
  if is_backend_impact_path "${changed_path}"; then
    BACKEND_IMPACT_PATHS="${BACKEND_IMPACT_PATHS}${changed_path}"$'\n'
  fi
done <<< "${CHANGED_PATHS}"
BACKEND_IMPACT_PATHS="${BACKEND_IMPACT_PATHS%$'\n'}"

# The unresolved P1/P2 review-comment policy is a backend-host merge gate. Frontend-only
# PRs still get their own console-web validation, but should not be blocked by backend
# review policy threads.
if [[ -n "${BACKEND_IMPACT_PATHS}" ]]; then
  echo "Backend-impact paths changed:"
  printf '%s\n' "${BACKEND_IMPACT_PATHS}"
else
  echo "No backend-impact paths changed; skipped unresolved P1/P2 review-comment gate."
  exit 0
fi

if [[ "${GITHUB_EVENT_NAME:-}" != "pull_request" && -z "${RESOLVED_PR_NUMBER}" ]]; then
  echo "Not a pull_request context; skipped GitHub review/comment gates."
  exit 0
fi

python3 - <<'PY'
import json
import os
import re
import subprocess
import sys
from pathlib import Path

repo = os.environ.get("GITHUB_REPOSITORY", "")
if "/" not in repo:
    print("GITHUB_REPOSITORY is required for pull_request host policy guard", file=sys.stderr)
    raise SystemExit(1)
owner, name = repo.split("/", 1)

pr_number = os.environ.get("RESOLVED_PR_NUMBER") or os.environ.get("PR_NUMBER")
if not pr_number:
    event_path = os.environ.get("GITHUB_EVENT_PATH")
    if event_path and Path(event_path).is_file():
        payload = json.loads(Path(event_path).read_text())
        pr_number = str(payload.get("pull_request", {}).get("number", ""))
if not pr_number:
    print("Pull request number is required for host policy guard", file=sys.stderr)
    raise SystemExit(1)

def gh_json(args):
    result = subprocess.run(["gh", "api", *args], text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode != 0:
        print(result.stderr, file=sys.stderr, end="")
        raise SystemExit(result.returncode)
    return json.loads(result.stdout or "null")


def gh_graphql(query, **fields):
    args = ["graphql", "-f", f"query={query}"]
    for key, value in fields.items():
        if value is not None:
            args.extend(["-F", f"{key}={value}"])
    return gh_json(args)

priority_patterns = [
    ("P1", re.compile(r"\[P1\]")),
    ("P2", re.compile(r"\[P2\]")),
    ("P1", re.compile(r"^\s*P1\s*:", re.M)),
    ("P2", re.compile(r"^\s*P2\s*:", re.M)),
    ("P1", re.compile(r"priority\s*:\s*P1", re.I)),
    ("P2", re.compile(r"priority\s*:\s*P2", re.I)),
    ("P1", re.compile(r"P1\s+Badge|badge/P1[-_]", re.I)),
    ("P2", re.compile(r"P2\s+Badge|badge/P2[-_]", re.I)),
    ("P1", re.compile(r"fkst:review-comment:v1[^>]*priority=[\"']P1[\"']")),
    ("P2", re.compile(r"fkst:review-comment:v1[^>]*priority=[\"']P2[\"']")),
]

def priority_for(body):
    text = body or ""
    for priority, pattern in priority_patterns:
        if pattern.search(text):
            return priority
    return None

def excerpt(body):
    text = re.sub(r"\s+", " ", (body or "").strip())
    return text[:237] + "..." if len(text) > 240 else text

review_threads_query = r'''
query($owner: String!, $name: String!, $number: Int!, $after: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      reviewThreads(first: 100, after: $after) {
        pageInfo { hasNextPage endCursor }
        nodes {
          isResolved
          path
          comments(first: 50) {
            nodes { body url path }
          }
        }
      }
    }
  }
}
'''

pr_comments_query = r'''
query($owner: String!, $name: String!, $number: Int!, $after: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      comments(first: 100, after: $after) {
        pageInfo { hasNextPage endCursor }
        nodes { body url }
      }
    }
  }
}
'''

blockers = []
after = None
while True:
    response = gh_graphql(review_threads_query, owner=owner, name=name, number=pr_number, after=after)
    if response.get("errors"):
        print(json.dumps(response["errors"], indent=2), file=sys.stderr)
        raise SystemExit(1)
    data = response.get("data") or {}
    repository = data.get("repository") or {}
    pull_request = repository.get("pullRequest") or {}
    threads = pull_request.get("reviewThreads") or {"nodes": [], "pageInfo": {}}
    for thread in threads.get("nodes") or []:
        if thread.get("isResolved") is True:
            continue
        for comment in (thread.get("comments") or {}).get("nodes") or []:
            priority = priority_for(comment.get("body"))
            if priority in {"P1", "P2"}:
                blockers.append({
                    "priority": priority,
                    "path": thread.get("path") or comment.get("path") or "",
                    "url": comment.get("url") or "",
                    "excerpt": excerpt(comment.get("body")),
                })
    page = threads.get("pageInfo") or {}
    if not page.get("hasNextPage"):
        break
    after = page.get("endCursor")

after = None
while True:
    response = gh_graphql(pr_comments_query, owner=owner, name=name, number=pr_number, after=after)
    if response.get("errors"):
        print(json.dumps(response["errors"], indent=2), file=sys.stderr)
        raise SystemExit(1)
    data = response.get("data") or {}
    repository = data.get("repository") or {}
    pull_request = repository.get("pullRequest") or {}
    comments = pull_request.get("comments") or {"nodes": [], "pageInfo": {}}
    for comment in comments.get("nodes") or []:
        priority = priority_for(comment.get("body"))
        if priority in {"P1", "P2"}:
            blockers.append({
                "priority": priority,
                "path": "PR comment",
                "url": comment.get("url") or "",
                "excerpt": excerpt(comment.get("body")),
            })
    page = comments.get("pageInfo") or {}
    if not page.get("hasNextPage"):
        break
    after = page.get("endCursor")

if blockers:
    print("Unresolved P1/P2 review comments block merge:", file=sys.stderr)
    for blocker in blockers[:10]:
        print(f"- {blocker['priority']} {blocker['path']} {blocker['url']} {blocker['excerpt']}", file=sys.stderr)
    raise SystemExit(1)

print("No unresolved P1/P2 review comments found.")
PY
