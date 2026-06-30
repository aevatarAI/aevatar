#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ZERO_SHA="0000000000000000000000000000000000000000"

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

if [[ -n "${GITHUB_BASE_SHA:-}" && -n "${GITHUB_HEAD_SHA:-}" ]]; then
  DIFF_TARGET="$(resolve_diff_target_from_shas "${GITHUB_BASE_SHA}" "${GITHUB_HEAD_SHA}")" \
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
PRODUCT_PATHS=""
while IFS= read -r changed_path; do
  [[ -n "${changed_path}" ]] || continue
  case "${changed_path}" in
    LICENSE|CHANGELOG|README.md|.fkst/*|.github/*|docs/*|test/*|tests/*|tools/*|workflows/*|*.md|*.txt|*.yml|*.yaml|*.json|*.toml)
      continue
      ;;
  esac
  PRODUCT_PATHS="${PRODUCT_PATHS}${changed_path}"$'\n'
done <<< "${CHANGED_PATHS}"
PRODUCT_PATHS="${PRODUCT_PATHS%$'\n'}"

if [[ -n "${PRODUCT_PATHS}" ]]; then
  echo "Product/runtime-impact paths changed:"
  printf '%s\n' "${PRODUCT_PATHS}"
else
  echo "No product/runtime-impact paths changed; runtime verification evidence is not required."
fi

if [[ "${GITHUB_EVENT_NAME:-}" != "pull_request" && -z "${PR_NUMBER:-}" ]]; then
  echo "Not a pull_request context; skipped GitHub review/comment gates."
  exit 0
fi

python3 - "${PRODUCT_PATHS}" <<'PY'
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

pr_number = os.environ.get("PR_NUMBER")
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

query = r'''
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

blockers = []
after = None
while True:
    response = gh_graphql(query, owner=owner, name=name, number=pr_number, after=after)
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

if blockers:
    print("Unresolved P1/P2 review comments block merge:", file=sys.stderr)
    for blocker in blockers[:10]:
        print(f"- {blocker['priority']} {blocker['path']} {blocker['url']} {blocker['excerpt']}", file=sys.stderr)
    raise SystemExit(1)

print("No unresolved P1/P2 review comments found.")
PY
