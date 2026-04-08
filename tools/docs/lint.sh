#!/usr/bin/env bash
# tools/docs/lint.sh — Validate docs frontmatter
# Checks: all docs/canon/ and docs/decisions/ files have required frontmatter fields
# Required fields: title, status, owner
# Exit 1 on first failure (CI gate mode) or accumulate all errors (report mode)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DOCS_DIR="$REPO_ROOT/docs"

ERRORS=0
CHECKED=0

check_frontmatter() {
  local file="$1"
  local relpath="${file#$REPO_ROOT/}"

  # Check frontmatter delimiters exist
  if ! head -1 "$file" | grep -q '^---$'; then
    echo "FAIL: $relpath — missing frontmatter (no opening ---)"
    ERRORS=$((ERRORS + 1))
    return
  fi

  # Extract frontmatter block (between first and second ---)
  local fm
  fm=$(awk 'NR==1{next} /^---$/{exit} {print}' "$file")

  # Check required fields
  for field in title status owner; do
    if ! echo "$fm" | grep -q "^${field}:"; then
      echo "FAIL: $relpath — missing required field: $field"
      ERRORS=$((ERRORS + 1))
    fi
  done

  CHECKED=$((CHECKED + 1))
}

# Lint canonical docs
if [ -d "$DOCS_DIR/canon" ]; then
  for file in "$DOCS_DIR"/canon/*.md; do
    [ -f "$file" ] || continue
    check_frontmatter "$file"
  done
fi

# Lint decision docs
if [ -d "$DOCS_DIR/decisions" ]; then
  for file in "$DOCS_DIR"/decisions/*.md; do
    [ -f "$file" ] || continue
    check_frontmatter "$file"
  done
fi

# Check decision file naming: must start with NNNN-
if [ -d "$DOCS_DIR/decisions" ]; then
  for file in "$DOCS_DIR"/decisions/*.md; do
    [ -f "$file" ] || continue
    basename=$(basename "$file")
    if ! echo "$basename" | grep -qE '^[0-9]{4}-'; then
      echo "FAIL: docs/decisions/$basename — must start with NNNN- (e.g., 0001-topic.md)"
      ERRORS=$((ERRORS + 1))
    fi
  done
fi

# Check canonical file naming: no date prefix, kebab-case
if [ -d "$DOCS_DIR/canon" ]; then
  for file in "$DOCS_DIR"/canon/*.md; do
    [ -f "$file" ] || continue
    basename=$(basename "$file")
    if echo "$basename" | grep -qE '^[0-9]{4}-'; then
      echo "FAIL: docs/canon/$basename — canonical docs must not have date prefix"
      ERRORS=$((ERRORS + 1))
    fi
  done
fi

echo ""
if [ "$ERRORS" -gt 0 ]; then
  echo "docs lint: FAILED — $ERRORS error(s) in $CHECKED file(s)"
  exit 1
else
  echo "docs lint: PASSED — $CHECKED file(s) checked, 0 errors"
  exit 0
fi
