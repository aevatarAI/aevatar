#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Refactor (iter18/cluster-019-strict-coverage-aevatar-slnx):
#   Old pattern: coverage_quality_guard.sh 吞 test fail 继续 emit coverage;split-test-guards 漏跑 22 orphan project
#   New principle: strict-coverage fail-fast(不吞 fail);aevatar.slnx-or-slow-test 唯一测试权威;
#   retire slnf 测试执行(只保留 slnf build boundary);slow-tests 独立 CI job
normalize_paths() {
  sed 's#\\#/#g; s#^\./##; s#//*#/#g'
}

tmp_dir="$(mktemp -d)"
trap 'rm -rf "${tmp_dir}"' EXIT

test_projects="${tmp_dir}/test-projects.txt"
slnx_test_projects="${tmp_dir}/slnx-test-projects.txt"
slow_test_projects="${tmp_dir}/slow-test-projects.txt"
owned_test_projects="${tmp_dir}/owned-test-projects.txt"

find test -name '*.csproj' -type f | normalize_paths | sort -u > "${test_projects}"

sed -n 's/.*<Project Path="\([^"]*\.csproj\)".*/\1/p' aevatar.slnx \
  | normalize_paths \
  | awk '/^test\/.*\.csproj$/ { print }' \
  | sort -u > "${slnx_test_projects}"

sed -n 's/.*dotnet test "\([^"]*\.csproj\)".*/\1/p' tools/ci/slow_test_guards.sh \
  | normalize_paths \
  | sort -u > "${slow_test_projects}"

slow_project_count="$(wc -l < "${slow_test_projects}" | tr -d '[:space:]')"
if [[ "${slow_project_count}" != "1" ]]; then
  echo "Expected exactly one slow-owned test project in tools/ci/slow_test_guards.sh; found ${slow_project_count}."
  cat "${slow_test_projects}"
  exit 1
fi

while IFS= read -r slnx_test_project; do
  if [[ ! -f "${slnx_test_project}" ]]; then
    echo "aevatar.slnx test entry points to a missing project: ${slnx_test_project}"
    exit 1
  fi
done < "${slnx_test_projects}"

while IFS= read -r slow_test_project; do
  if [[ ! -f "${slow_test_project}" ]]; then
    echo "slow_test_guards.sh points to a missing project: ${slow_test_project}"
    exit 1
  fi
done < "${slow_test_projects}"

cat "${slnx_test_projects}" "${slow_test_projects}" | sort -u > "${owned_test_projects}"

orphan_projects="$(comm -23 "${test_projects}" "${owned_test_projects}")"
if [[ -n "${orphan_projects}" ]]; then
  echo "Found test projects outside the authoritative test surface."
  echo "Every test/*.csproj must be included in aevatar.slnx or owned by tools/ci/slow_test_guards.sh:"
  echo "${orphan_projects}"
  exit 1
fi

echo "Test solution ownership guard passed."
