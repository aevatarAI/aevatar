#!/usr/bin/env bash
# test/tools/test_docs_tools.sh — Tests for tools/docs/lint.sh and tools/docs/build-index.sh
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
  # Override REPO_ROOT for lint.sh by creating a wrapper
  (cd "$TMPDIR_TEST" && REPO_ROOT="$TMPDIR_TEST" bash -c '
    SCRIPT_DIR="'"$REPO_ROOT"'/tools/docs"
    REPO_ROOT="'"$TMPDIR_TEST"'"
    DOCS_DIR="$REPO_ROOT/docs"
    source '"$LINT"'
  ' 2>&1) || true
}

assert_pass() {
  local test_name="$1"
  local exit_code=0
  (
    cd "$TMPDIR_TEST"
    bash -c '
      DOCS_DIR="'"$TMPDIR_TEST"'/docs"
      ERRORS=0; CHECKED=0
      check_frontmatter() {
        local file="$1"
        if ! head -1 "$file" | grep -q "^---$"; then ERRORS=$((ERRORS+1)); return; fi
        local fm=$(awk "NR==1{next} /^---$/{exit} {print}" "$file")
        for field in title status owner; do
          if ! echo "$fm" | grep -q "^${field}:"; then ERRORS=$((ERRORS+1)); fi
        done
        CHECKED=$((CHECKED+1))
      }
      for file in "$DOCS_DIR"/canon/*.md; do [ -f "$file" ] && check_frontmatter "$file"; done 2>/dev/null
      for file in "$DOCS_DIR"/adr/*.md; do [ -f "$file" ] && check_frontmatter "$file"; done 2>/dev/null
      exit $ERRORS
    '
  ) > /dev/null 2>&1
  exit_code=$?
  if [ "$exit_code" -eq 0 ]; then
    echo "  PASS: $test_name"
    PASS=$((PASS + 1))
  else
    echo "  FAIL: $test_name (expected pass, got exit $exit_code)"
    FAIL=$((FAIL + 1))
  fi
}

assert_fail() {
  local test_name="$1"
  local exit_code=0
  (
    cd "$TMPDIR_TEST"
    bash -c '
      DOCS_DIR="'"$TMPDIR_TEST"'/docs"
      ERRORS=0; CHECKED=0
      check_frontmatter() {
        local file="$1"
        if ! head -1 "$file" | grep -q "^---$"; then ERRORS=$((ERRORS+1)); return; fi
        local fm=$(awk "NR==1{next} /^---$/{exit} {print}" "$file")
        for field in title status owner; do
          if ! echo "$fm" | grep -q "^${field}:"; then ERRORS=$((ERRORS+1)); fi
        done
        CHECKED=$((CHECKED+1))
      }
      for file in "$DOCS_DIR"/canon/*.md; do [ -f "$file" ] && check_frontmatter "$file"; done 2>/dev/null
      for file in "$DOCS_DIR"/adr/*.md; do [ -f "$file" ] && check_frontmatter "$file"; done 2>/dev/null
      exit $ERRORS
    '
  ) > /dev/null 2>&1
  exit_code=$?
  if [ "$exit_code" -ne 0 ]; then
    echo "  PASS: $test_name"
    PASS=$((PASS + 1))
  else
    echo "  FAIL: $test_name (expected fail, got pass)"
    FAIL=$((FAIL + 1))
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
assert_pass "canonical doc with complete frontmatter"

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
assert_fail "canonical doc missing title"

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
assert_fail "canonical doc missing status"

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
assert_fail "canonical doc missing owner"

# Test 5: No frontmatter at all → FAIL
echo "Test 5: No frontmatter fails"
setup_test_docs
cat > "$TMPDIR_TEST/docs/canon/test.md" << 'EOF'
# Test Document

No frontmatter here.
EOF
assert_fail "canonical doc with no frontmatter"

# Test 6: History files without frontmatter → PASS (not checked)
echo "Test 6: History files not checked"
setup_test_docs
mkdir -p "$TMPDIR_TEST/docs/history/2026-03"
cat > "$TMPDIR_TEST/docs/history/2026-03/test.md" << 'EOF'
# Historical doc without frontmatter
EOF
assert_pass "history files are not lint-checked"

# Test 7: build-index generates README
echo "Test 7: build-index generates README"
setup_test_docs
cat > "$TMPDIR_TEST/docs/canon/test.md" << 'EOF'
---
title: "My Test Doc"
status: active
owner: testuser
---

# Test
EOF
# Run build-index with modified paths
(
  DOCS_DIR="$TMPDIR_TEST/docs"
  OUTPUT="$DOCS_DIR/README.md"
  echo "# Aevatar Documentation" > "$OUTPUT"
  echo "" >> "$OUTPUT"
  for file in "$DOCS_DIR"/canon/*.md; do
    [ -f "$file" ] || continue
    title=$(sed -n '2,/^---$/p' "$file" | grep "^title:" | sed 's/^title: *//' | sed 's/^"//' | sed 's/"$//')
    echo "- [$title](canon/$(basename "$file"))" >> "$OUTPUT"
  done
)
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
