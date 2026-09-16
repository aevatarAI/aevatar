#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/required_ci_workflow_event_guard.sh"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

write_valid_workflows() {
  local root="$1"

  mkdir -p "${root}/.github/workflows"
  cat > "${root}/.github/workflows/ci.yml" <<'YAML'
name: ci

on:
  pull_request:
  push:
    branches:
      - main
      - dev
  schedule:
    - cron: "0 3 * * *"
  workflow_dispatch:

jobs:
  fast-gates:
    runs-on: ubuntu-latest
    steps:
      - run: echo fast
  console-web:
    runs-on: ubuntu-latest
    steps:
      - run: echo console
  coverage-quality:
    runs-on: ubuntu-latest
    steps:
      - run: echo coverage
YAML

  cat > "${root}/.github/workflows/fkst-review-policy.yml" <<'YAML'
name: fkst-review-policy

on:
  pull_request_review:
    types: [submitted, edited, dismissed]
  pull_request_review_comment:
    types: [created, edited, deleted]
  issue_comment:
    types: [created, edited, deleted]

jobs:
  fkst-review-policy:
    runs-on: ubuntu-latest
    steps:
      - run: echo review
YAML
}

valid_root="${TMP_DIR}/valid"
write_valid_workflows "${valid_root}"
AEVATAR_CI_WORKFLOW_GUARD_ROOT="${valid_root}" bash "${GUARD}" > "${TMP_DIR}/valid.out"
if ! rg -q "required CI workflow event guard passed" "${TMP_DIR}/valid.out"; then
  echo "Expected valid workflow split to pass."
  cat "${TMP_DIR}/valid.out"
  exit 1
fi

forbidden_root="${TMP_DIR}/forbidden"
write_valid_workflows "${forbidden_root}"
perl -0pi -e 's/  push:\n/  issue_comment:\n    types: [created]\n  push:\n/' "${forbidden_root}/.github/workflows/ci.yml"

if AEVATAR_CI_WORKFLOW_GUARD_ROOT="${forbidden_root}" bash "${GUARD}" > "${TMP_DIR}/forbidden.out" 2>&1; then
  echo "Expected ci.yml review/comment trigger regression to fail."
  cat "${TMP_DIR}/forbidden.out"
  exit 1
fi

if ! rg -q "must not subscribe to issue_comment" "${TMP_DIR}/forbidden.out"; then
  echo "Expected forbidden event failure to name issue_comment."
  cat "${TMP_DIR}/forbidden.out"
  exit 1
fi

conflict_root="${TMP_DIR}/conflict"
write_valid_workflows "${conflict_root}"
perl -0pi -e 's/  fkst-review-policy:\n/  fast-gates:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo conflict\n  fkst-review-policy:\n/' "${conflict_root}/.github/workflows/fkst-review-policy.yml"

if AEVATAR_CI_WORKFLOW_GUARD_ROOT="${conflict_root}" bash "${GUARD}" > "${TMP_DIR}/conflict.out" 2>&1; then
  echo "Expected review policy required-check job name conflict to fail."
  cat "${TMP_DIR}/conflict.out"
  exit 1
fi

if ! rg -q "must not reuse required check job names: fast-gates" "${TMP_DIR}/conflict.out"; then
  echo "Expected required-check job name conflict failure."
  cat "${TMP_DIR}/conflict.out"
  exit 1
fi

echo "required CI workflow event guard tests passed"
