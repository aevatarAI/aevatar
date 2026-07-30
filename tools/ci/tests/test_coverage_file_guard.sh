#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
GUARD="${SCRIPT_DIR}/../test_coverage_file_guard.sh"

tmp_dir="$(mktemp -d)"
trap 'rm -rf "${tmp_dir}"' EXIT

case_id=0
fixture=""

new_fixture() {
  case_id=$((case_id + 1))
  fixture="${tmp_dir}/case-${case_id}"
  mkdir -p \
    "${fixture}/current/test/Sample" \
    "${fixture}/current/tools/ci" \
    "${fixture}/base/test/Sample" \
    "${fixture}/base/tools/ci"
}

write_allowlist_header() {
  local root="$1"
  printf 'path\tmax_lines\towner_issue\treason\n' \
    > "${fixture}/${root}/tools/ci/test_coverage_file_allowlist.tsv"
}

assert_passes() {
  local output
  if ! output="$(
    AEVATAR_TEST_COVERAGE_FILE_REPO_ROOT="${fixture}/current" \
      AEVATAR_TEST_COVERAGE_FILE_BASE_ALLOWLIST="${fixture}/base/tools/ci/test_coverage_file_allowlist.tsv" \
      AEVATAR_TEST_COVERAGE_FILE_BASE_ROOT="${fixture}/base" \
      AEVATAR_TEST_COVERAGE_FILE_TEST_MODE=1 \
      bash "${GUARD}" 2>&1
  )"; then
    echo "Expected coverage-file guard to pass."
    echo "${output}"
    exit 1
  fi
}

assert_fails_with() {
  local expected="$1"
  local output
  if output="$(
    AEVATAR_TEST_COVERAGE_FILE_REPO_ROOT="${fixture}/current" \
      AEVATAR_TEST_COVERAGE_FILE_BASE_ALLOWLIST="${fixture}/base/tools/ci/test_coverage_file_allowlist.tsv" \
      AEVATAR_TEST_COVERAGE_FILE_BASE_ROOT="${fixture}/base" \
      AEVATAR_TEST_COVERAGE_FILE_TEST_MODE=1 \
      bash "${GUARD}" 2>&1
  )"; then
    echo "Expected coverage-file guard to fail with: ${expected}"
    exit 1
  fi
  if [[ "${output}" != *"${expected}"* ]]; then
    echo "Coverage-file guard failed without expected diagnostic: ${expected}"
    echo "${output}"
    exit 1
  fi
}

init_git_fixture() {
  git -C "${fixture}/current" init -q -b dev
  git -C "${fixture}/current" config user.name "Coverage Guard Tests"
  git -C "${fixture}/current" config user.email "coverage-guard@example.invalid"
  git -C "${fixture}/current" config commit.gpgsign false
}

commit_git_fixture() {
  local message="$1"
  git -C "${fixture}/current" add test tools/ci
  git -C "${fixture}/current" commit -q -m "${message}"
}

assert_git_fails_with() {
  local expected="$1"
  shift
  local output
  if output="$(
    env \
      AEVATAR_TEST_COVERAGE_FILE_REPO_ROOT="${fixture}/current" \
      AEVATAR_TEST_COVERAGE_FILE_TEST_MODE=1 \
      "$@" \
      bash "${GUARD}" 2>&1
  )"; then
    echo "Expected Git-backed coverage-file guard to fail with: ${expected}"
    exit 1
  fi
  if [[ "${output}" != *"${expected}"* ]]; then
    echo "Git-backed coverage-file guard failed without expected diagnostic: ${expected}"
    echo "${output}"
    exit 1
  fi
}

new_fixture
write_allowlist_header current
write_allowlist_header base
if output="$(
  AEVATAR_TEST_COVERAGE_FILE_REPO_ROOT="${fixture}/current" \
    bash "${GUARD}" 2>&1
)"; then
  echo "Expected production guard to reject a fixture root override without test mode."
  exit 1
fi
if [[ "${output}" != *"is test-only"* ]]; then
  echo "Production guard rejected the root override without the expected diagnostic."
  echo "${output}"
  exit 1
fi

new_fixture
printf 'line one\nline two\n' > "${fixture}/current/test/Sample/LegacyCoverageTests.cs"
printf 'line one\nline two\n' > "${fixture}/base/test/Sample/LegacyCoverageTests.cs"
write_allowlist_header current
write_allowlist_header base
printf 'test/Sample/LegacyCoverageTests.cs\t2\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
printf 'test/Sample/LegacyCoverageTests.cs\t2\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/base/tools/ci/test_coverage_file_allowlist.tsv"
assert_passes

new_fixture
printf 'public class NewCoverageTests {}\n' > "${fixture}/current/test/Sample/NewCoverageTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf 'line one\nline two\n' > "${fixture}/current/test/Sample/LegacyCoverageTests.cs"
printf 'line one\n' > "${fixture}/base/test/Sample/LegacyCoverageTests.cs"
write_allowlist_header current
write_allowlist_header base
printf 'test/Sample/LegacyCoverageTests.cs\t1\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
printf 'test/Sample/LegacyCoverageTests.cs\t1\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/base/tools/ci/test_coverage_file_allowlist.tsv"
assert_fails_with 'exceeds frozen coverage-test budget 1'

new_fixture
write_allowlist_header current
write_allowlist_header base
printf 'test/Sample/MissingCoverageTests.cs\t1\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
printf 'test/Sample/MissingCoverageTests.cs\t1\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/base/tools/ci/test_coverage_file_allowlist.tsv"
assert_fails_with 'allowlist entry is stale'

new_fixture
mkdir -p "${fixture}/current/test/Sample/obj/Debug"
printf 'public class GeneratedCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/obj/Debug/GeneratedCoverageTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_passes

new_fixture
printf 'public class LegacyCoverageTests {}\n' > "${fixture}/current/test/Sample/LegacyCoverageTests.cs"
printf 'public class LegacyCoverageTests {}\n' > "${fixture}/base/test/Sample/LegacyCoverageTests.cs"
write_allowlist_header current
write_allowlist_header base
printf 'test/Sample/LegacyCoverageTests.cs\t1\tissue-2058\thistorical behavior suite\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
assert_fails_with 'owner_issue must match #NNNN'

new_fixture
mkdir -p "${fixture}/current/test/Sample/Generated"
printf 'public class GeneratedCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/Generated/GeneratedCoverageTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf 'public class HiddenCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf '%s\n' \
  'var raw = """public class RawCoverageTests {}""";' \
  'var verbatim = @"public class VerbatimCoverageTests {}";' \
  'var regular = "public class StringCoverageTests {}";' \
  'var interpolated = $"public class InterpolatedCoverageTests {{}}";' \
  'var interpolatedRaw = $$"""public class InterpolatedRawCoverageTests {}""";' \
  '// public class LineCommentCoverageTests {}' \
  '/* public class BlockCommentCoverageTests {} */' \
  'public class BehaviorTests {}' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_passes

new_fixture
printf '%s\n' \
  'public class BehaviorTests' \
  '{' \
  '    private static readonly string Source = $"""prefix {"""public class FakeCoverageTests {}"""} suffix""";' \
  '}' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_passes

new_fixture
printf 'public class\n@HiddenCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf '%s\n' \
  '#if false' \
  '"""' \
  '#endif' \
  'public class HiddenCoverageTests {}' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf '\xEF\xBB\xBF#if false\n"""\n#endif\npublic class HiddenCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf '\xFFpublic class BehaviorTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'source is not valid UTF-8'

new_fixture
printf 'public partial class LegacyCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorFragmentTests.cs"
printf '\xFFpublic partial class LegacyCoverageTests {}\n' \
  > "${fixture}/base/test/Sample/BehaviorFragmentTests.cs"
write_allowlist_header current
write_allowlist_header base
printf 'test/Sample/BehaviorFragmentTests.cs\t1\t#2058\thistorical partial suite fragment\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
assert_fails_with 'source is not valid UTF-8'

new_fixture
printf '%s\n' \
  'var source = """' \
  '#if false' \
  'public class FakeCoverageTests {}' \
  '#endif' \
  '""";' \
  'public class BehaviorTests {}' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_passes

new_fixture
printf '%s\n' \
  'var source = """' \
  '#if false' \
  'public class FakeCoverageTests {}' \
  '""";' \
  'public class BehaviorTests {}' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_passes

new_fixture
printf 'public /* declaration separator */ partial class @HiddenCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf '%s\n' \
  'public class' \
  '#line 1 "generated"' \
  'HiddenCoverageTests {}' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf '%s\n' \
  'public class' \
  '#if USE_BEHAVIOR_NAME' \
  'BehaviorTests' \
  '#else' \
  'HiddenCoverageTests' \
  '#endif' \
  '{}' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf 'namespace Sample; [Collection("sample")] public class HiddenCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf 'public class ÜberCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf 'public class Hidden\\u0043overageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf 'public class HiddenCoverage\u200CTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf 'public class HiddenCoverage\\u200CTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf '%s\n' \
  'public class @class {}' \
  'public class BehaviorTests { private @class SupportCoverageTests; }' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_passes

new_fixture
printf 'public record HiddenRecordCoverageTests;\n' \
  > "${fixture}/current/test/Sample/BehaviorRecordTests.cs"
write_allowlist_header current
write_allowlist_header base
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf 'public partial class LegacyCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/LegacyCoverageTests.cs"
printf 'public partial class LegacyCoverageTests {}\n' \
  > "${fixture}/base/test/Sample/LegacyCoverageTests.cs"
printf 'public partial class LegacyCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorFragmentTests.cs"
write_allowlist_header current
write_allowlist_header base
printf 'test/Sample/LegacyCoverageTests.cs\t1\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
printf 'test/Sample/LegacyCoverageTests.cs\t1\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/base/tools/ci/test_coverage_file_allowlist.tsv"
assert_fails_with 'new coverage-named test files or classes are not allowed'

new_fixture
printf 'public class NewCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/NewCoverageTests.cs"
write_allowlist_header current
write_allowlist_header base
printf 'test/Sample/NewCoverageTests.cs\t1\t#2058\tnewly added bucket\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
assert_fails_with 'new allowlist entries are not allowed'

new_fixture
printf 'line one\nline two\n' > "${fixture}/current/test/Sample/LegacyCoverageTests.cs"
printf 'line one\n' > "${fixture}/base/test/Sample/LegacyCoverageTests.cs"
write_allowlist_header current
write_allowlist_header base
printf 'test/Sample/LegacyCoverageTests.cs\t2\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
printf 'test/Sample/LegacyCoverageTests.cs\t1\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/base/tools/ci/test_coverage_file_allowlist.tsv"
assert_fails_with 'allowlist budget increased from 1 to 2'

new_fixture
printf 'public partial class LegacyCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorFragmentTests.cs"
printf 'public partial class LegacyCoverageTests {}\n' \
  > "${fixture}/base/test/Sample/BehaviorFragmentTests.cs"
write_allowlist_header current
write_allowlist_header base
printf 'test/Sample/BehaviorFragmentTests.cs\t1\t#2058\thistorical partial suite fragment\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
assert_passes

new_fixture
init_git_fixture
printf 'public class BehaviorTests {}\n' \
  > "${fixture}/current/test/Sample/BehaviorTests.cs"
write_allowlist_header current
commit_git_fixture 'Create baseline'
base_sha="$(git -C "${fixture}/current" rev-parse HEAD)"
printf 'public class NewCoverageTests {}\n' \
  > "${fixture}/current/test/Sample/NewCoverageTests.cs"
printf 'test/Sample/NewCoverageTests.cs\t1\t#2058\tnewly added bucket\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
assert_git_fails_with \
  'new allowlist entries are not allowed' \
  AEVATAR_TEST_COVERAGE_FILE_BASE_REF="${base_sha}"

new_fixture
init_git_fixture
printf 'line one\n' > "${fixture}/current/test/Sample/LegacyCoverageTests.cs"
write_allowlist_header current
printf 'test/Sample/LegacyCoverageTests.cs\t1\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
commit_git_fixture 'Create baseline'
base_sha="$(git -C "${fixture}/current" rev-parse HEAD)"
printf 'line one\nline two\n' > "${fixture}/current/test/Sample/LegacyCoverageTests.cs"
sed -i.bak $'s/\t1\t/\t2\t/' \
  "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
assert_git_fails_with \
  'allowlist budget increased from 1 to 2' \
  AEVATAR_TEST_COVERAGE_FILE_BASE_REF="${base_sha}"

new_fixture
init_git_fixture
printf 'line one\n' > "${fixture}/current/test/Sample/LegacyCoverageTests.cs"
write_allowlist_header current
printf 'test/Sample/LegacyCoverageTests.cs\t1\t#2058\thistorical behavior suite\n' \
  >> "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
commit_git_fixture 'Create baseline'
base_sha="$(git -C "${fixture}/current" rev-parse HEAD)"
printf 'line one\nline two\n' > "${fixture}/current/test/Sample/LegacyCoverageTests.cs"
sed -i.bak $'s/\t1\t/\t2\t/' \
  "${fixture}/current/tools/ci/test_coverage_file_allowlist.tsv"
commit_git_fixture 'Increase budget'
current_sha="$(git -C "${fixture}/current" rev-parse HEAD)"
git -C "${fixture}/current" update-ref refs/remotes/origin/dev "${current_sha}"
printf '{"before":"%s","after":"%s","ref":"refs/heads/dev"}\n' \
  "${base_sha}" "${current_sha}" > "${fixture}/push-event.json"
assert_git_fails_with \
  'allowlist budget increased from 1 to 2' \
  GITHUB_ACTIONS=true \
  GITHUB_EVENT_NAME=push \
  GITHUB_EVENT_PATH="${fixture}/push-event.json"

new_fixture
init_git_fixture
write_allowlist_header current
commit_git_fixture 'Create initial commit'
current_sha="$(git -C "${fixture}/current" rev-parse HEAD)"
git -C "${fixture}/current" update-ref refs/remotes/origin/dev "${current_sha}"
printf '{"before":"0000000000000000000000000000000000000000","after":"%s","ref":"refs/heads/dev"}\n' \
  "${current_sha}" > "${fixture}/push-event.json"
assert_git_fails_with \
  'Unable to resolve a trustworthy baseline commit for GitHub push event' \
  GITHUB_ACTIONS=true \
  GITHUB_EVENT_NAME=push \
  GITHUB_EVENT_PATH="${fixture}/push-event.json"

new_fixture
init_git_fixture
write_allowlist_header current
commit_git_fixture 'Create initial commit'
current_sha="$(git -C "${fixture}/current" rev-parse HEAD)"
printf '{"before":"%s","after":"%s","ref":"refs/heads/dev"}\n' \
  "${current_sha}" "${current_sha}" > "${fixture}/push-event.json"
assert_git_fails_with \
  'Unable to resolve a trustworthy baseline commit for GitHub push event' \
  GITHUB_ACTIONS=true \
  GITHUB_EVENT_NAME=push \
  GITHUB_EVENT_PATH="${fixture}/push-event.json"

echo "test coverage-file guard tests passed"
