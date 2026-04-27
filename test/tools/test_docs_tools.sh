#!/usr/bin/env bash
# test/tools/test_docs_tools.sh — Tests for tools/docs/lint.sh and tools/docs/build-index.sh
#
# Tests invoke the real scripts via DOCS_DIR override so CI guards stay in sync
# with what we exercise here. Adding a rule to lint.sh without a test below will
# leave the rule unverified.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
LINT="$REPO_ROOT/tools/docs/lint.sh"
BUILD_INDEX="$REPO_ROOT/tools/docs/build-index.sh"

PASS=0
FAIL=0
TMPDIR_TEST=$(mktemp -d)
trap "rm -rf $TMPDIR_TEST" EXIT

setup_test_docs() {
  rm -rf "$TMPDIR_TEST/docs"
  mkdir -p "$TMPDIR_TEST/docs/canon" "$TMPDIR_TEST/docs/adr"
}

run_lint() {
  DOCS_DIR="$TMPDIR_TEST/docs" bash "$LINT" > /dev/null 2>&1
}

assert_lint_passes() {
  local test_name="$1"
  if run_lint; then
    echo "  PASS: $test_name"
    PASS=$((PASS + 1))
  else
    echo "  FAIL: $test_name (expected lint pass, got fail)"
    FAIL=$((FAIL + 1))
  fi
}

assert_lint_fails() {
  local test_name="$1"
  if run_lint; then
    echo "  FAIL: $test_name (expected lint fail, got pass)"
    FAIL=$((FAIL + 1))
  else
    echo "  PASS: $test_name"
    PASS=$((PASS + 1))
  fi
}

echo "=== Test Suite: docs tools ==="
echo ""

# Test 1: Canonical doc with complete frontmatter → PASS
echo "Test 1: Complete frontmatter passes"
setup_test_docs
cat > "$TMPDIR_TEST/docs/canon/test.md" << 'EOF'
---
title: "Test Doc"
status: active
owner: testuser
---

# Test
EOF
assert_lint_passes "canonical doc with complete frontmatter"

# Test 2: Missing title → FAIL
echo "Test 2: Missing title fails"
setup_test_docs
cat > "$TMPDIR_TEST/docs/canon/test.md" << 'EOF'
---
status: active
owner: testuser
---

# Test
EOF
assert_lint_fails "canonical doc missing title"

# Test 3: Missing status → FAIL
echo "Test 3: Missing status fails"
setup_test_docs
cat > "$TMPDIR_TEST/docs/canon/test.md" << 'EOF'
---
title: "Test"
owner: testuser
---

# Test
EOF
assert_lint_fails "canonical doc missing status"

# Test 4: Missing owner → FAIL
echo "Test 4: Missing owner fails"
setup_test_docs
cat > "$TMPDIR_TEST/docs/canon/test.md" << 'EOF'
---
title: "Test"
status: active
---

# Test
EOF
assert_lint_fails "canonical doc missing owner"

# Test 5: No frontmatter at all → FAIL
echo "Test 5: No frontmatter fails"
setup_test_docs
cat > "$TMPDIR_TEST/docs/canon/test.md" << 'EOF'
# Test Document

No frontmatter here.
EOF
assert_lint_fails "canonical doc with no frontmatter"

# Test 6: History files without frontmatter → PASS (not checked)
echo "Test 6: History files not checked"
setup_test_docs
mkdir -p "$TMPDIR_TEST/docs/history/2026-03"
cat > "$TMPDIR_TEST/docs/history/2026-03/test.md" << 'EOF'
# Historical doc without frontmatter
EOF
assert_lint_passes "history files are not lint-checked"

# Test 7: ADR with valid frontmatter and unique number → PASS
echo "Test 7: ADR with unique number passes"
setup_test_docs
cat > "$TMPDIR_TEST/docs/adr/0001-foo.md" << 'EOF'
---
title: "Foo"
status: accepted
owner: testuser
---

# ADR-0001: Foo
EOF
cat > "$TMPDIR_TEST/docs/adr/0002-bar.md" << 'EOF'
---
title: "Bar"
status: accepted
owner: testuser
---

# ADR-0002: Bar
EOF
assert_lint_passes "two ADRs with distinct numbers"

# Test 8: Duplicate ADR numbers → FAIL
echo "Test 8: Duplicate ADR numbers fail"
setup_test_docs
cat > "$TMPDIR_TEST/docs/adr/0017-foo.md" << 'EOF'
---
title: "Foo"
status: accepted
owner: testuser
---

# ADR-0017: Foo
EOF
cat > "$TMPDIR_TEST/docs/adr/0017-bar.md" << 'EOF'
---
title: "Bar"
status: accepted
owner: testuser
---

# ADR-0017: Bar
EOF
assert_lint_fails "two ADRs with the same number 0017"

# Test 9: ADR file without NNNN- prefix → FAIL
echo "Test 9: ADR without numeric prefix fails"
setup_test_docs
cat > "$TMPDIR_TEST/docs/adr/no-prefix.md" << 'EOF'
---
title: "No prefix"
status: accepted
owner: testuser
---

# No prefix
EOF
assert_lint_fails "ADR without NNNN- prefix"

# Test 10: Legacy docs/decisions/ directory → FAIL
echo "Test 10: Legacy docs/decisions/ rejected"
setup_test_docs
mkdir -p "$TMPDIR_TEST/docs/decisions"
cat > "$TMPDIR_TEST/docs/decisions/0001-old.md" << 'EOF'
---
title: "Old"
status: accepted
owner: testuser
---

# ADR-0001: Old
EOF
assert_lint_fails "legacy docs/decisions/ directory present"

# Test 11: Canonical doc with date prefix → FAIL
echo "Test 11: Canonical doc with date prefix fails"
setup_test_docs
cat > "$TMPDIR_TEST/docs/canon/2026-04-27-bad.md" << 'EOF'
---
title: "Bad"
status: active
owner: testuser
---

# Bad
EOF
assert_lint_fails "canonical doc with date prefix"

# Test 12: build-index produces README with overridden DOCS_DIR
echo "Test 12: build-index honours DOCS_DIR override"
setup_test_docs
cat > "$TMPDIR_TEST/docs/canon/test.md" << 'EOF'
---
title: "My Test Doc"
status: active
owner: testuser
---

# Test
EOF
DOCS_DIR="$TMPDIR_TEST/docs" bash "$BUILD_INDEX" > /dev/null 2>&1
if [ -f "$TMPDIR_TEST/docs/README.md" ] && grep -q "My Test Doc" "$TMPDIR_TEST/docs/README.md"; then
  echo "  PASS: build-index generates README with doc titles"
  PASS=$((PASS + 1))
else
  echo "  FAIL: build-index did not generate expected README"
  FAIL=$((FAIL + 1))
fi

echo ""
echo "=== Results: $PASS passed, $FAIL failed ==="
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
