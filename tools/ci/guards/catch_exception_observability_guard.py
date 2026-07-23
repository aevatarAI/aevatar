#!/usr/bin/env python3
"""Detect broad catch blocks that hide failures behind debug-only or empty fallbacks."""

from __future__ import annotations

import argparse
import csv
from dataclasses import dataclass
from pathlib import Path
import re
import sys


BROAD_CATCH_RE = re.compile(r"\bcatch\s*(?:\(\s*Exception(?:\s+\w+)?\s*\))?")
SPECIFIC_EXCEPTION_RE = re.compile(r"\bcatch\s*\(\s*(?!Exception\b)[A-Za-z_][A-Za-z0-9_.<>]*")
VISIBLE_LOG_RE = re.compile(r"\bLog(?:Warning|Error|Critical)\s*\(")
DEBUG_LOG_RE = re.compile(r"\bLogDebug\s*\(")
RETHROW_RE = re.compile(r"\bthrow\s*;|\bthrow\s+")
COMMITTED_FAILURE_RE = re.compile(
    r"\b(PersistDomainEventAsync|PersistDomainEventsAsync|PublishAsync)\s*\([^;]*(Failed|Rejected|Error)",
    re.DOTALL,
)
RETURN_NULL_RE = re.compile(r"\breturn\s+null\s*;")
COMMENT_RE = re.compile(r"//.*?$|/\*.*?\*/", re.DOTALL | re.MULTILINE)


@dataclass(frozen=True)
class CatchBlock:
    path: Path
    line: int
    header: str
    body: str


@dataclass(frozen=True)
class Finding:
    path: Path
    line: int
    message: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="*", type=Path, help="Files or directories to scan.")
    parser.add_argument("--root", type=Path, default=Path.cwd(), help="Repository root for display paths.")
    parser.add_argument("--baseline", type=Path, help="Existing findings that are not allowed to grow.")
    return parser.parse_args()


def iter_csharp_files(paths: list[Path]) -> list[Path]:
    selected = paths or [Path("src"), Path("agents")]
    files: set[Path] = set()
    for selected_path in selected:
        if selected_path.is_file() and selected_path.suffix == ".cs":
            if is_scannable(selected_path):
                files.add(selected_path)
            continue

        if not selected_path.exists():
            continue

        for path in selected_path.rglob("*.cs"):
            if is_scannable(path):
                files.add(path)

    return sorted(files)


def is_scannable(path: Path) -> bool:
    parts = set(path.parts)
    if {"bin", "obj"} & parts:
        return False
    name = path.name
    return not (
        name.endswith(".g.cs")
        or name.endswith(".Designer.cs")
        or "Generated" in parts
    )


def find_matching_brace(text: str, open_index: int) -> int | None:
    depth = 0
    in_string = False
    in_verbatim_string = False
    in_char = False
    in_line_comment = False
    in_block_comment = False
    i = open_index
    while i < len(text):
        ch = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""

        if in_line_comment:
            if ch in "\r\n":
                in_line_comment = False
            i += 1
            continue
        if in_block_comment:
            if ch == "*" and nxt == "/":
                in_block_comment = False
                i += 2
                continue
            i += 1
            continue
        if in_string:
            if in_verbatim_string:
                if ch == '"' and nxt == '"':
                    i += 2
                    continue
                if ch == '"':
                    in_string = False
                    in_verbatim_string = False
            else:
                if ch == "\\":
                    i += 2
                    continue
                if ch == '"':
                    in_string = False
            i += 1
            continue
        if in_char:
            if ch == "\\":
                i += 2
                continue
            if ch == "'":
                in_char = False
            i += 1
            continue

        if ch == "/" and nxt == "/":
            in_line_comment = True
            i += 2
            continue
        if ch == "/" and nxt == "*":
            in_block_comment = True
            i += 2
            continue
        if ch == "@" and nxt == '"':
            in_string = True
            in_verbatim_string = True
            i += 2
            continue
        if ch == '"':
            in_string = True
            i += 1
            continue
        if ch == "'":
            in_char = True
            i += 1
            continue

        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return None


def find_catch_body_open_brace(text: str, start_index: int) -> int | None:
    in_string = False
    in_verbatim_string = False
    in_char = False
    in_line_comment = False
    in_block_comment = False
    paren_depth = 0
    i = start_index
    while i < len(text):
        ch = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""

        if in_line_comment:
            if ch in "\r\n":
                in_line_comment = False
            i += 1
            continue
        if in_block_comment:
            if ch == "*" and nxt == "/":
                in_block_comment = False
                i += 2
                continue
            i += 1
            continue
        if in_string:
            if in_verbatim_string:
                if ch == '"' and nxt == '"':
                    i += 2
                    continue
                if ch == '"':
                    in_string = False
                    in_verbatim_string = False
            else:
                if ch == "\\":
                    i += 2
                    continue
                if ch == '"':
                    in_string = False
            i += 1
            continue
        if in_char:
            if ch == "\\":
                i += 2
                continue
            if ch == "'":
                in_char = False
            i += 1
            continue

        if ch == "/" and nxt == "/":
            in_line_comment = True
            i += 2
            continue
        if ch == "/" and nxt == "*":
            in_block_comment = True
            i += 2
            continue
        if ch == "@" and nxt == '"':
            in_string = True
            in_verbatim_string = True
            i += 2
            continue
        if ch == '"':
            in_string = True
            i += 1
            continue
        if ch == "'":
            in_char = True
            i += 1
            continue

        if ch == "(":
            paren_depth += 1
        elif ch == ")" and paren_depth > 0:
            paren_depth -= 1
        elif ch == "{" and paren_depth == 0:
            return i
        i += 1
    return None


def iter_catches(path: Path) -> list[CatchBlock]:
    text = path.read_text(encoding="utf-8")
    catches: list[CatchBlock] = []
    for match in re.finditer(r"\bcatch\b", text):
        open_brace = find_catch_body_open_brace(text, match.end())
        if open_brace is None:
            continue
        header = text[match.start():open_brace]
        if not BROAD_CATCH_RE.search(header):
            continue
        if SPECIFIC_EXCEPTION_RE.search(header):
            continue
        close_brace = find_matching_brace(text, open_brace)
        if close_brace is None:
            continue
        line = text.count("\n", 0, match.start()) + 1
        catches.append(CatchBlock(path, line, header, text[open_brace + 1:close_brace]))
    return catches


def strip_comments(text: str) -> str:
    return COMMENT_RE.sub("", text)


def evaluate(block: CatchBlock) -> Finding | None:
    body = strip_comments(block.body).strip()
    if not body:
        return Finding(block.path, block.line, "empty broad catch block")

    has_visible_log = bool(VISIBLE_LOG_RE.search(body))
    has_debug_log = bool(DEBUG_LOG_RE.search(body))
    has_rethrow = bool(RETHROW_RE.search(body))
    has_committed_failure = bool(COMMITTED_FAILURE_RE.search(body))

    if has_debug_log and not (has_visible_log or has_rethrow or has_committed_failure):
        return Finding(block.path, block.line, "broad catch logs only at Debug")

    if RETURN_NULL_RE.search(body) and not (has_visible_log or has_rethrow or has_committed_failure):
        return Finding(block.path, block.line, "broad catch returns null without visible failure signal")

    return None


def display_path(path: Path, root: Path) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def main() -> int:
    args = parse_args()
    files = iter_csharp_files(args.paths)
    findings: list[Finding] = []
    for path in files:
        for block in iter_catches(path):
            finding = evaluate(block)
            if finding is not None:
                findings.append(finding)

    baseline = load_baseline(args.baseline) if args.baseline else set()
    active_keys = {finding_key(finding, args.root) for finding in findings}
    new_findings = [finding for finding in findings if finding_key(finding, args.root) not in baseline]
    resolved_findings = sorted(baseline - active_keys)

    if new_findings:
        for finding in new_findings:
            print(f"{display_path(finding.path, args.root)}:{finding.line}: {finding.message}")
        print(
            "Broad catch recovery paths must use visible logging, rethrow, or committed failure events."
        )
        return 1

    if resolved_findings:
        print("Catch exception observability baseline has stale entries:")
        for path, line, message in resolved_findings:
            print(f"{path}:{line}: {message}")
        print("Remove resolved entries from the baseline.")
        return 1

    print("Catch exception observability guard passed.")
    return 0


def finding_key(finding: Finding, root: Path) -> tuple[str, int, str]:
    return (display_path(finding.path, root), finding.line, finding.message)


def load_baseline(path: Path | None) -> set[tuple[str, int, str]]:
    if path is None or not path.exists():
        return set()

    entries: set[tuple[str, int, str]] = set()
    with path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.reader(handle, delimiter="\t")
        for line_number, row in enumerate(reader, start=1):
            if not row or all(not value.strip() for value in row):
                continue
            if line_number == 1 and row == ["path", "line", "message"]:
                continue
            if len(row) != 3:
                raise ValueError(f"{path}:{line_number}: expected 3 tab-separated fields")
            entry_path, line_text, message = [value.strip() for value in row]
            entries.add((entry_path, int(line_text), message))
    return entries


if __name__ == "__main__":
    sys.exit(main())
