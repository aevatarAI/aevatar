#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

python3 <<'PY'
from __future__ import annotations

import csv
import os
from dataclasses import dataclass
from pathlib import Path
import sys


ALLOWLIST_PATH = Path(
    os.environ.get(
        "AEVATAR_TEST_COVERAGE_FILE_ALLOWLIST",
        "tools/ci/test_coverage_file_allowlist.tsv",
    )
)
ROOT = Path(os.environ.get("AEVATAR_TEST_COVERAGE_FILE_ROOT", "test"))


@dataclass(frozen=True)
class AllowlistEntry:
    path: str
    max_lines: int
    owner_issue: str
    reason: str


def repo_path(path: Path) -> str:
    return path.as_posix()


def is_generated_or_build_output(path: Path) -> bool:
    lowered_parts = {part.lower() for part in path.parts}
    return bool(lowered_parts.intersection({"bin", "obj", "generated"}))


def count_lines(path: Path) -> int:
    text = path.read_text(encoding="utf-8", errors="replace")
    return len(text.splitlines()) if text else 0


def parse_owner_issue(value: str) -> bool:
    return len(value) >= 2 and value[0] == "#" and value[1:].isdigit()


def load_allowlist(path: Path) -> tuple[dict[str, AllowlistEntry], list[str]]:
    if not path.exists():
        return {}, [f"Allowlist file is missing: {path}"]

    entries: dict[str, AllowlistEntry] = {}
    errors: list[str] = []
    with path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.reader(handle, delimiter="\t")
        for line_number, row in enumerate(reader, start=1):
            if not row or all(not value.strip() for value in row):
                continue
            if line_number == 1 and row == [
                "path",
                "max_lines",
                "owner_issue",
                "reason",
            ]:
                continue
            if len(row) != 4:
                errors.append(
                    f"{path}:{line_number}: expected 4 tab-separated fields, got {len(row)}"
                )
                continue

            entry_path, max_lines_text, owner_issue, reason = [
                value.strip() for value in row
            ]
            if not entry_path or not max_lines_text or not owner_issue or not reason:
                errors.append(f"{path}:{line_number}: allowlist fields must be non-empty")
                continue
            if not entry_path.startswith("test/") or not entry_path.endswith("CoverageTests.cs"):
                errors.append(
                    f"{path}:{line_number}: path must be a test/*CoverageTests.cs file"
                )
                continue
            try:
                max_lines = int(max_lines_text)
            except ValueError:
                errors.append(f"{path}:{line_number}: max_lines must be an integer")
                continue
            if max_lines < 0:
                errors.append(f"{path}:{line_number}: max_lines must be non-negative")
                continue
            if not parse_owner_issue(owner_issue):
                errors.append(f"{path}:{line_number}: owner_issue must match #NNNN")
                continue
            if entry_path in entries:
                errors.append(f"{path}:{line_number}: duplicate allowlist path: {entry_path}")
                continue

            entries[entry_path] = AllowlistEntry(
                path=entry_path,
                max_lines=max_lines,
                owner_issue=owner_issue,
                reason=reason,
            )

    return entries, errors


def iter_coverage_named_tests(root: Path) -> list[Path]:
    if not root.exists():
        return []
    return sorted(
        path
        for path in root.rglob("*CoverageTests.cs")
        if path.is_file() and not is_generated_or_build_output(path)
    )


def main() -> int:
    allowlist, allowlist_errors = load_allowlist(ALLOWLIST_PATH)
    if allowlist_errors:
        print("Test coverage-file guard allowlist is invalid:")
        for error in allowlist_errors:
            print(f"  {error}")
        return 1

    violations: list[str] = []
    seen: set[str] = set()
    for path in iter_coverage_named_tests(ROOT):
        relative = repo_path(path)
        seen.add(relative)
        line_count = count_lines(path)
        entry = allowlist.get(relative)
        if entry is None:
            violations.append(
                f"{relative}:{line_count}: new *CoverageTests.cs files are not allowed; "
                "name the test after behavior instead"
            )
            continue

        if line_count > entry.max_lines:
            violations.append(
                f"{relative}:{line_count}: exceeds frozen coverage-test budget "
                f"{entry.max_lines}; split or rename before growing"
            )

    stale = sorted(path for path in allowlist if path not in seen)
    if stale:
        violations.extend(
            f"{path}: allowlist entry is stale; remove it after renaming or deleting the file"
            for path in stale
        )

    if violations:
        print("Test coverage-file naming guard failed.")
        for violation in violations:
            print(f"  {violation}")
        print(
            "Avoid generic *CoverageTests.cs buckets. Use behavior-focused test names, "
            "or shrink an existing historical file before changing its budget."
        )
        return 1

    print("test_coverage_file_guard: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
PY
