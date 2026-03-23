#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PORT="${SYMPHONY_PORT:-8081}"

for cmd in gh codex symphony dotnet; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "missing required command: $cmd" >&2
    exit 1
  fi
done

gh auth status >/dev/null

export GITHUB_TOKEN="${GITHUB_TOKEN:-$(gh auth token)}"
export SYMPHONY_DEFAULT_BRANCH="${SYMPHONY_DEFAULT_BRANCH:-dev}"
export SYMPHONY_WORKSPACE_ROOT="${SYMPHONY_WORKSPACE_ROOT:-$HOME/.symphony-workspaces/aevatar}"

mkdir -p "$SYMPHONY_WORKSPACE_ROOT"

cd "$REPO_ROOT"
exec symphony ./WORKFLOW.md --port "$PORT" --pretty-logs
