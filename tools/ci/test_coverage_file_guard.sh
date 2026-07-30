#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DEFAULT_REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
TEST_MODE="${AEVATAR_TEST_COVERAGE_FILE_TEST_MODE:-}"
TEST_ONLY_OVERRIDE_NAMES=(
  AEVATAR_TEST_COVERAGE_FILE_REPO_ROOT
  AEVATAR_TEST_COVERAGE_FILE_ALLOWLIST
  AEVATAR_TEST_COVERAGE_FILE_ROOT
  AEVATAR_TEST_COVERAGE_FILE_BASE_ALLOWLIST
  AEVATAR_TEST_COVERAGE_FILE_BASE_ROOT
  AEVATAR_TEST_COVERAGE_FILE_BASE_REF
)
if [[ "${TEST_MODE}" != "1" ]]; then
  for override_name in "${TEST_ONLY_OVERRIDE_NAMES[@]}"; do
    if [[ -n "${!override_name:-}" ]]; then
      echo "${override_name} is test-only; set AEVATAR_TEST_COVERAGE_FILE_TEST_MODE=1 for isolated guard fixtures."
      exit 1
    fi
  done
fi
REPO_ROOT="${AEVATAR_TEST_COVERAGE_FILE_REPO_ROOT:-${DEFAULT_REPO_ROOT}}"
cd "${REPO_ROOT}"

python3 <<'PY'
from __future__ import annotations

import csv
import io
import json
import os
import subprocess
import unicodedata
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
import sys
from typing import TextIO


ALLOWLIST_PATH = Path(
    os.environ.get(
        "AEVATAR_TEST_COVERAGE_FILE_ALLOWLIST",
        "tools/ci/test_coverage_file_allowlist.tsv",
    )
)
ROOT = Path(os.environ.get("AEVATAR_TEST_COVERAGE_FILE_ROOT", "test"))
BASE_ALLOWLIST_PATH = os.environ.get("AEVATAR_TEST_COVERAGE_FILE_BASE_ALLOWLIST")
BASE_ROOT_PATH = os.environ.get("AEVATAR_TEST_COVERAGE_FILE_BASE_ROOT")
BASE_REF = os.environ.get("AEVATAR_TEST_COVERAGE_FILE_BASE_REF")


@dataclass(frozen=True)
class AllowlistEntry:
    path: str
    max_lines: int
    owner_issue: str
    reason: str


@dataclass(frozen=True)
class ConditionalSource:
    branches: list[list[object]]


class SourceScanError(RuntimeError):
    pass


def repo_path(path: Path) -> str:
    return path.as_posix()


def is_generated_or_build_output(path: Path) -> bool:
    lowered_parts = {part.lower() for part in path.parts}
    return bool(lowered_parts.intersection({"bin", "obj"}))


def read_utf8_source(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8-sig")
    except UnicodeDecodeError as error:
        raise SourceScanError(
            f"{repo_path(path)}: source is not valid UTF-8"
        ) from error


def count_lines(path: Path) -> int:
    text = read_utf8_source(path)
    return len(text.splitlines()) if text else 0


def parse_owner_issue(value: str) -> bool:
    return len(value) >= 2 and value[0] == "#" and value[1:].isdigit()


def parse_allowlist(
    handle: TextIO,
    source: str,
) -> tuple[dict[str, AllowlistEntry], list[str]]:
    entries: dict[str, AllowlistEntry] = {}
    errors: list[str] = []
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
                f"{source}:{line_number}: expected 4 tab-separated fields, got {len(row)}"
            )
            continue

        entry_path, max_lines_text, owner_issue, reason = [
            value.strip() for value in row
        ]
        if not entry_path or not max_lines_text or not owner_issue or not reason:
            errors.append(f"{source}:{line_number}: allowlist fields must be non-empty")
            continue
        if not entry_path.startswith("test/") or not entry_path.endswith(".cs"):
            errors.append(
                f"{source}:{line_number}: path must be a test/*.cs coverage artifact"
            )
            continue
        try:
            max_lines = int(max_lines_text)
        except ValueError:
            errors.append(f"{source}:{line_number}: max_lines must be an integer")
            continue
        if max_lines < 0:
            errors.append(f"{source}:{line_number}: max_lines must be non-negative")
            continue
        if not parse_owner_issue(owner_issue):
            errors.append(f"{source}:{line_number}: owner_issue must match #NNNN")
            continue
        if entry_path in entries:
            errors.append(f"{source}:{line_number}: duplicate allowlist path: {entry_path}")
            continue

        entries[entry_path] = AllowlistEntry(
            path=entry_path,
            max_lines=max_lines,
            owner_issue=owner_issue,
            reason=reason,
        )

    return entries, errors


def load_allowlist(path: Path) -> tuple[dict[str, AllowlistEntry], list[str]]:
    if not path.exists():
        return {}, [f"Allowlist file is missing: {path}"]
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as handle:
            return parse_allowlist(handle, str(path))
    except UnicodeDecodeError:
        return {}, [f"{path}: allowlist is not valid UTF-8"]


def load_allowlist_text(
    text: str,
    source: str,
) -> tuple[dict[str, AllowlistEntry], list[str]]:
    return parse_allowlist(io.StringIO(text), source)


def blank_range(characters: list[str], start: int, end: int) -> None:
    for index in range(start, end):
        if characters[index] not in {"\r", "\n"}:
            characters[index] = " "


def quote_run_length(text: str, start: int) -> int:
    end = start
    while end < len(text) and text[end] == '"':
        end += 1
    return end - start


def character_run_length(text: str, start: int, character: str) -> int:
    end = start
    while end < len(text) and text[end] == character:
        end += 1
    return end - start


def scan_line_comment(text: str, start: int) -> int:
    newline = text.find("\n", start + 2)
    return len(text) if newline == -1 else newline


def scan_block_comment(text: str, start: int) -> int:
    closing = text.find("*/", start + 2)
    return len(text) if closing == -1 else closing + 2


def is_preprocessor_directive_start(text: str, start: int) -> bool:
    line_start = text.rfind("\n", 0, start) + 1
    return text[line_start:start].strip() == ""


def scan_character_literal(text: str, quote: int) -> int:
    index = quote + 1
    while index < len(text):
        if text[index] in {"\r", "\n"}:
            return index
        if text[index] == "\\":
            index += 2
            continue
        if text[index] == "'":
            return index + 1
        index += 1
    return len(text)


def scan_regular_string(text: str, quote: int) -> int:
    index = quote + 1
    while index < len(text):
        if text[index] in {"\r", "\n"}:
            return index
        if text[index] == "\\":
            index += 2
            continue
        if text[index] == '"':
            return index + 1
        index += 1
    return len(text)


def scan_verbatim_string(text: str, quote: int) -> int:
    index = quote + 1
    while index < len(text):
        if text.startswith('""', index):
            index += 2
            continue
        if text[index] == '"':
            return index + 1
        index += 1
    return len(text)


def scan_interpolation_expression(
    text: str,
    start: int,
    closing_braces: int,
) -> int:
    index = start
    brace_depth = 0
    closing = "}" * closing_braces
    while index < len(text):
        if text.startswith("//", index):
            index = scan_line_comment(text, index)
            continue
        if text.startswith("/*", index):
            index = scan_block_comment(text, index)
            continue
        literal_end = scan_literal(text, index)
        if literal_end is not None:
            index = literal_end
            continue
        if brace_depth == 0 and text.startswith(closing, index):
            return index + closing_braces
        if text[index] == "{":
            brace_depth += 1
        elif text[index] == "}" and brace_depth > 0:
            brace_depth -= 1
        index += 1
    return len(text)


def scan_raw_string(
    text: str,
    quote: int,
    delimiter_length: int,
    interpolation_braces: int = 0,
) -> int:
    index = quote + delimiter_length
    while index < len(text):
        run_length = quote_run_length(text, index) if text[index] == '"' else 0
        if run_length >= delimiter_length:
            return index + delimiter_length
        if interpolation_braces and text[index] == "{":
            brace_run = 1
            while index + brace_run < len(text) and text[index + brace_run] == "{":
                brace_run += 1
            if brace_run >= interpolation_braces:
                index += brace_run - interpolation_braces
                index = scan_interpolation_expression(
                    text,
                    index + interpolation_braces,
                    interpolation_braces,
                )
                continue
        index += max(run_length, 1)
    return len(text)


def scan_interpolated_string(text: str, quote: int, verbatim: bool) -> int:
    index = quote + 1
    brace_depth = 0
    while index < len(text):
        if brace_depth == 0:
            if not verbatim and text[index] in {"\r", "\n"}:
                return index
            if not verbatim and text[index] == "\\":
                index += 2
                continue
            if verbatim and text.startswith('""', index):
                index += 2
                continue
            if text[index] == '"':
                return index + 1
            if text.startswith("{{", index) or text.startswith("}}", index):
                index += 2
                continue
            if text[index] == "{":
                brace_depth = 1
            index += 1
            continue

        if text.startswith("//", index):
            index = scan_line_comment(text, index)
            continue
        if text.startswith("/*", index):
            index = scan_block_comment(text, index)
            continue
        literal_end = scan_literal(text, index)
        if literal_end is not None:
            index = literal_end
            continue
        if text[index] == "{":
            brace_depth += 1
        elif text[index] == "}":
            brace_depth -= 1
        index += 1
    return len(text)


def scan_literal(text: str, start: int) -> int | None:
    if text[start] == "'":
        return scan_character_literal(text, start)

    if text[start] == "$":
        prefix_end = start
        while prefix_end < len(text) and text[prefix_end] == "$":
            prefix_end += 1
        if prefix_end < len(text) and text[prefix_end] == '"':
            delimiter_length = quote_run_length(text, prefix_end)
            if delimiter_length >= 3:
                return scan_raw_string(
                    text,
                    prefix_end,
                    delimiter_length,
                    interpolation_braces=prefix_end - start,
                )
            if prefix_end == start + 1:
                return scan_interpolated_string(text, prefix_end, verbatim=False)
        if text.startswith('@"', prefix_end) and prefix_end == start + 1:
            return scan_interpolated_string(text, prefix_end + 1, verbatim=True)

    if text.startswith('@$"', start):
        return scan_interpolated_string(text, start + 2, verbatim=True)
    if text.startswith('@"', start):
        return scan_verbatim_string(text, start + 1)
    if text[start] == '"':
        delimiter_length = quote_run_length(text, start)
        if delimiter_length >= 3:
            return scan_raw_string(text, start, delimiter_length)
        return scan_regular_string(text, start)
    return None


def sanitize_csharp(
    text: str,
    *,
    preserve_preprocessor_directives: bool = False,
) -> str:
    characters = list(text)
    index = 0
    while index < len(text):
        if text[index] == "#" and is_preprocessor_directive_start(text, index):
            end = scan_line_comment(text, index)
            if not preserve_preprocessor_directives:
                blank_range(characters, index, end)
            index = end
            continue
        if text.startswith("//", index):
            end = scan_line_comment(text, index)
            blank_range(characters, index, end)
            index = end
            continue
        if text.startswith("/*", index):
            end = scan_block_comment(text, index)
            blank_range(characters, index, end)
            index = end
            continue
        literal_end = scan_literal(text, index)
        if literal_end is not None:
            blank_range(characters, index, literal_end)
            index = literal_end
            continue
        index += 1
    return "".join(characters)


def conditional_directive(line: str) -> str | None:
    stripped = line.lstrip()
    if not stripped.startswith("#"):
        return None
    directive = stripped[1:].lstrip().split(maxsplit=1)
    if not directive:
        return None
    kind = directive[0]
    return kind if kind in {"if", "elif", "else", "endif"} else None


def parse_conditional_nodes(
    lines: list[str],
    start: int = 0,
    stop_at: frozenset[str] = frozenset(),
    *,
    sanitize_nonconditional_directives: bool = True,
    allow_unterminated_as_text: bool = False,
) -> tuple[list[object], int, str | None]:
    nodes: list[object] = []
    index = start
    while index < len(lines):
        kind = conditional_directive(lines[index])
        if kind in stop_at:
            return nodes, index, kind
        if kind != "if":
            line = lines[index]
            nodes.append(
                sanitize_csharp(line)
                if sanitize_nonconditional_directives
                and line.lstrip().startswith("#")
                else line
            )
            index += 1
            continue

        conditional_start = index
        branches: list[list[object]] = []
        branch, index, terminator = parse_conditional_nodes(
            lines,
            index + 1,
            frozenset({"elif", "else", "endif"}),
            sanitize_nonconditional_directives=sanitize_nonconditional_directives,
            allow_unterminated_as_text=allow_unterminated_as_text,
        )
        branches.append(branch)
        while terminator == "elif":
            branch, index, terminator = parse_conditional_nodes(
                lines,
                index + 1,
                frozenset({"elif", "else", "endif"}),
                sanitize_nonconditional_directives=sanitize_nonconditional_directives,
                allow_unterminated_as_text=allow_unterminated_as_text,
            )
            branches.append(branch)

        has_else = terminator == "else"
        if has_else:
            branch, index, terminator = parse_conditional_nodes(
                lines,
                index + 1,
                frozenset({"endif"}),
                sanitize_nonconditional_directives=sanitize_nonconditional_directives,
                allow_unterminated_as_text=allow_unterminated_as_text,
            )
            branches.append(branch)
        else:
            branches.append([])
        if terminator != "endif":
            if allow_unterminated_as_text:
                nodes.extend(lines[conditional_start:])
                return nodes, len(lines), None
            raise SourceScanError("unterminated conditional compilation block")

        nodes.append(ConditionalSource(branches))
        index += 1
    return nodes, index, None


def parse_preprocessor_nodes(
    text: str,
    *,
    sanitize_nonconditional_directives: bool = True,
    allow_unterminated_as_text: bool = False,
) -> list[object]:
    nodes, _, terminator = parse_conditional_nodes(
        text.splitlines(keepends=True),
        sanitize_nonconditional_directives=sanitize_nonconditional_directives,
        allow_unterminated_as_text=allow_unterminated_as_text,
    )
    if terminator is not None:
        raise SourceScanError(f"unexpected #{terminator} directive")
    return nodes


def identifier_escape(text: str, start: int) -> tuple[str, int] | None:
    if text.startswith("\\u", start):
        digits = 4
    elif text.startswith("\\U", start):
        digits = 8
    else:
        return None
    end = start + 2 + digits
    if end > len(text):
        return None
    value = text[start + 2 : end]
    if any(character not in "0123456789abcdefABCDEF" for character in value):
        return None
    try:
        return chr(int(value, 16)), end
    except (ValueError, OverflowError):
        return None


def is_identifier_start(character: str) -> bool:
    return character == "_" or unicodedata.category(character) in {
        "Lu",
        "Ll",
        "Lt",
        "Lm",
        "Lo",
        "Nl",
    }


def is_identifier_part(character: str) -> bool:
    return is_identifier_start(character) or unicodedata.category(character) in {
        "Mn",
        "Mc",
        "Nd",
        "Pc",
        "Cf",
    }


def read_identifier(text: str, start: int) -> tuple[str, int, bool] | None:
    index = start
    verbatim = index < len(text) and text[index] == "@"
    if verbatim:
        index += 1
    if index >= len(text):
        return None

    escaped = identifier_escape(text, index)
    if escaped is not None:
        character, index = escaped
    else:
        character = text[index]
        index += 1
    if not is_identifier_start(character):
        return None

    characters = [character]
    while index < len(text):
        escaped = identifier_escape(text, index)
        if escaped is not None:
            character, next_index = escaped
        else:
            character = text[index]
            next_index = index + 1
        if not is_identifier_part(character):
            break
        if unicodedata.category(character) != "Cf":
            characters.append(character)
        index = next_index
    return "".join(characters), index, verbatim


def next_identifier(text: str, start: int) -> tuple[str, int, bool] | None:
    index = start
    while index < len(text) and text[index].isspace():
        index += 1
    return read_identifier(text, index)


def consecutive_identifiers(
    text: str,
    start: int,
) -> list[tuple[str, int, bool]]:
    tokens: list[tuple[str, int, bool]] = []
    index = start
    while True:
        token = next_identifier(text, index)
        if token is None:
            return tokens
        tokens.append(token)
        _, index, _ = token


DECLARATION_SCAN_NORMAL = 0
DECLARATION_SCAN_AFTER_CLASS = 1
DECLARATION_SCAN_AFTER_RECORD = 2
DECLARATION_SCAN_NAMES = 3


@dataclass(frozen=True)
class LexicalFrame:
    kind: str
    delimiter_width: int = 0
    interpolation_width: int = 0
    brace_depth: int = 0


@dataclass(frozen=True)
class BranchScanState:
    declaration: int = DECLARATION_SCAN_NORMAL
    lexical_frames: tuple[LexicalFrame, ...] = ()


def scan_declaration_text(text: str, state: int) -> tuple[int, bool]:
    index = 0
    while index < len(text):
        if text[index].isspace():
            index += 1
            continue
        token = read_identifier(text, index)
        if token is None:
            state = DECLARATION_SCAN_NORMAL
            index += 1
            continue

        value, index, verbatim = token
        if state == DECLARATION_SCAN_NORMAL:
            if not verbatim and value == "class":
                state = DECLARATION_SCAN_AFTER_CLASS
            elif not verbatim and value == "record":
                state = DECLARATION_SCAN_AFTER_RECORD
            continue

        if state == DECLARATION_SCAN_AFTER_RECORD and not verbatim and value in {
            "class",
            "struct",
        }:
            state = DECLARATION_SCAN_AFTER_CLASS
            continue

        if value.endswith("CoverageTests"):
            return state, True
        state = DECLARATION_SCAN_NAMES
    return state, False


def literal_frame(text: str, start: int) -> tuple[LexicalFrame, int] | None:
    if text[start] == "'":
        return LexicalFrame("character"), start + 1

    if text[start] == "$":
        prefix_end = start
        while prefix_end < len(text) and text[prefix_end] == "$":
            prefix_end += 1
        if prefix_end < len(text) and text[prefix_end] == '"':
            delimiter_width = quote_run_length(text, prefix_end)
            if delimiter_width >= 3:
                return (
                    LexicalFrame(
                        "raw",
                        delimiter_width=delimiter_width,
                        interpolation_width=prefix_end - start,
                    ),
                    prefix_end + delimiter_width,
                )
            if prefix_end == start + 1:
                return LexicalFrame("regular", interpolation_width=1), prefix_end + 1
        if text.startswith('@"', prefix_end) and prefix_end == start + 1:
            return LexicalFrame("verbatim", interpolation_width=1), prefix_end + 2

    if text.startswith('@$"', start):
        return LexicalFrame("verbatim", interpolation_width=1), start + 3
    if text.startswith('@"', start):
        return LexicalFrame("verbatim"), start + 2
    if text[start] == '"':
        delimiter_width = quote_run_length(text, start)
        if delimiter_width >= 3:
            return (
                LexicalFrame("raw", delimiter_width=delimiter_width),
                start + delimiter_width,
            )
        return LexicalFrame("regular"), start + 1
    return None


def with_expression_depth(frame: LexicalFrame, depth: int) -> LexicalFrame:
    return LexicalFrame(
        frame.kind,
        delimiter_width=frame.delimiter_width,
        interpolation_width=frame.interpolation_width,
        brace_depth=depth,
    )


def sanitize_csharp_fragment(
    text: str,
    frames: tuple[LexicalFrame, ...],
) -> tuple[str, tuple[LexicalFrame, ...]]:
    characters = [character if character in {"\r", "\n"} else " " for character in text]
    index = 0
    while index < len(text):
        if not frames:
            if text[index] == "#" and is_preprocessor_directive_start(text, index):
                index = scan_line_comment(text, index)
                continue
            if text.startswith("//", index):
                index = scan_line_comment(text, index)
                continue
            if text.startswith("/*", index):
                frames += (LexicalFrame("block_comment"),)
                index += 2
                continue
            literal = literal_frame(text, index)
            if literal is not None:
                frame, index = literal
                frames += (frame,)
                continue
            characters[index] = text[index]
            index += 1
            continue

        frame = frames[-1]
        if frame.kind == "block_comment":
            if text.startswith("*/", index):
                frames = frames[:-1]
                index += 2
            else:
                index += 1
            continue

        if frame.kind in {"character", "regular"}:
            if text[index] in {"\r", "\n"}:
                frames = frames[:-1]
                continue
            if text[index] == "\\":
                index += min(2, len(text) - index)
                continue
            if text[index] == ("'" if frame.kind == "character" else '"'):
                frames = frames[:-1]
                index += 1
                continue
            if frame.interpolation_width:
                if text.startswith("{{", index) or text.startswith("}}", index):
                    index += 2
                    continue
                if text[index] == "{":
                    frames += (
                        LexicalFrame(
                            "expression",
                            interpolation_width=1,
                            brace_depth=1,
                        ),
                    )
                    index += 1
                    continue
            index += 1
            continue

        if frame.kind == "verbatim":
            if text.startswith('""', index):
                index += 2
                continue
            if text[index] == '"':
                frames = frames[:-1]
                index += 1
                continue
            if frame.interpolation_width:
                if text.startswith("{{", index) or text.startswith("}}", index):
                    index += 2
                    continue
                if text[index] == "{":
                    frames += (
                        LexicalFrame(
                            "expression",
                            interpolation_width=1,
                            brace_depth=1,
                        ),
                    )
                    index += 1
                    continue
            index += 1
            continue

        if frame.kind == "raw":
            if text[index] == '"':
                delimiter_width = quote_run_length(text, index)
                if delimiter_width >= frame.delimiter_width:
                    frames = frames[:-1]
                    index += frame.delimiter_width
                    continue
            if frame.interpolation_width and text[index] == "{":
                brace_width = character_run_length(text, index, "{")
                if brace_width >= frame.interpolation_width:
                    frames += (
                        LexicalFrame(
                            "expression",
                            interpolation_width=frame.interpolation_width,
                            brace_depth=1,
                        ),
                    )
                    index += frame.interpolation_width
                    continue
            index += 1
            continue

        if frame.kind != "expression":
            raise SourceScanError(f"unexpected lexical frame: {frame.kind}")

        if text.startswith("//", index):
            index = scan_line_comment(text, index)
            continue
        if text.startswith("/*", index):
            frames += (LexicalFrame("block_comment"),)
            index += 2
            continue
        literal = literal_frame(text, index)
        if literal is not None:
            nested_frame, index = literal
            frames += (nested_frame,)
            continue
        if text[index] == "{":
            frames = frames[:-1] + (
                with_expression_depth(frame, frame.brace_depth + 1),
            )
            index += 1
            continue
        if text[index] == "}":
            closing_width = character_run_length(text, index, "}")
            if frame.brace_depth == 1 and closing_width >= frame.interpolation_width:
                frames = frames[:-1]
                index += frame.interpolation_width
            else:
                frames = frames[:-1] + (
                    with_expression_depth(frame, max(frame.brace_depth - 1, 1)),
                )
                index += 1
            continue
        index += 1
    return "".join(characters), frames


def scan_conditional_declarations(
    nodes: list[object],
    states: set[int],
) -> tuple[set[int], bool]:
    for node in nodes:
        if isinstance(node, str):
            next_states: set[int] = set()
            for state in states:
                next_state, found = scan_declaration_text(node, state)
                if found:
                    return states, True
                next_states.add(next_state)
            states = next_states
            continue
        if not isinstance(node, ConditionalSource):
            raise SourceScanError("unexpected conditional compilation node")

        branch_states: set[int] = set()
        for branch in node.branches:
            next_states, found = scan_conditional_declarations(
                branch,
                states,
            )
            if found:
                return states, True
            branch_states.update(next_states)
        states = branch_states
    return states, False


def scan_fallback_conditional_declarations(
    nodes: list[object],
    states: set[BranchScanState],
) -> tuple[set[BranchScanState], bool]:
    for node in nodes:
        if isinstance(node, str):
            next_states: set[BranchScanState] = set()
            for state in states:
                source, lexical_frames = sanitize_csharp_fragment(
                    node,
                    state.lexical_frames,
                )
                declaration, found = scan_declaration_text(
                    source,
                    state.declaration,
                )
                if found:
                    return states, True
                next_states.add(BranchScanState(declaration, lexical_frames))
            states = next_states
            continue
        if not isinstance(node, ConditionalSource):
            raise SourceScanError("unexpected conditional compilation node")

        branch_states: set[BranchScanState] = set()
        for state in states:
            for branch in node.branches:
                next_states, found = scan_fallback_conditional_declarations(
                    branch,
                    {state},
                )
                if found:
                    return states, True
                for next_state in next_states:
                    frames = next_state.lexical_frames
                    initial_frames = state.lexical_frames
                    if (
                        len(frames) > len(initial_frames)
                        and frames[: len(initial_frames)] == initial_frames
                    ):
                        # An inactive branch may contain an incomplete token. It
                        # cannot extend past its own conditional boundary.
                        next_state = state
                    branch_states.add(next_state)
        states = branch_states
    return states, False


def has_coverage_class_declaration(text: str) -> bool:
    directive_source = sanitize_csharp(
        text,
        preserve_preprocessor_directives=True,
    )
    try:
        nodes = parse_preprocessor_nodes(directive_source)
        _, found = scan_conditional_declarations(
            nodes,
            {DECLARATION_SCAN_NORMAL},
        )
        if found:
            return True
    except SourceScanError:
        pass

    # Inactive branches may contain incomplete tokens that confuse a whole-file
    # lexical pass without making its reconstructed directive tree unbalanced.
    # Confirm every apparent miss while carrying lexical state per branch.
    try:
        nodes = parse_preprocessor_nodes(
            text,
            sanitize_nonconditional_directives=False,
            allow_unterminated_as_text=True,
        )
    except SourceScanError as error:
        if "unterminated conditional compilation block" in str(error):
            return has_coverage_class_declaration_in_variant(text)
        raise
    _, found = scan_fallback_conditional_declarations(
        nodes,
        {BranchScanState()},
    )
    return found


def has_coverage_class_declaration_in_variant(text: str) -> bool:
    source = sanitize_csharp(text)
    index = 0
    while index < len(source):
        token = read_identifier(source, index)
        if token is None:
            index += 1
            continue
        value, token_end, verbatim = token
        index = token_end
        if verbatim or value not in {"class", "record"}:
            continue

        name_tokens = consecutive_identifiers(source, token_end)
        if not name_tokens:
            continue
        name, _, name_verbatim = name_tokens[0]
        if value == "record" and not name_verbatim and name in {"class", "struct"}:
            name_tokens = name_tokens[1:]
        if any(candidate.endswith("CoverageTests") for candidate, _, _ in name_tokens):
            return True
    return False


def is_coverage_artifact(path: Path, text: str) -> bool:
    if path.name.endswith("CoverageTests.cs"):
        return True
    could_contain_escaped_identifier = "\\u" in text or "\\U" in text
    could_contain_format_character = not text.isascii() and any(
        unicodedata.category(character) == "Cf" for character in text
    )
    return (
        "CoverageTests" in text
        or could_contain_escaped_identifier
        or could_contain_format_character
    ) and has_coverage_class_declaration(text)


def iter_coverage_artifacts(root: Path) -> list[Path]:
    if not root.exists():
        return []
    artifacts: list[Path] = []
    for path in root.rglob("*.cs"):
        if not path.is_file() or is_generated_or_build_output(path):
            continue
        text = read_utf8_source(path)
        try:
            coverage_artifact = is_coverage_artifact(path, text)
        except SourceScanError as error:
            raise SourceScanError(f"{repo_path(path)}: {error}") from error
        if coverage_artifact:
            artifacts.append(path)
    return sorted(artifacts)


def run_git(*args: str) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            ["git", *args],
            check=False,
            capture_output=True,
            encoding="utf-8-sig",
        )
    except UnicodeDecodeError as error:
        raise SourceScanError(
            f"git {' '.join(args)}: source is not valid UTF-8"
        ) from error


def resolve_commit(ref: str) -> str | None:
    result = run_git("rev-parse", "--verify", f"{ref}^{{commit}}")
    return result.stdout.strip() if result.returncode == 0 else None


def resolve_ci_baseline(ref: str) -> str | None:
    resolved = resolve_commit(ref)
    current_head = resolve_commit("HEAD")
    if resolved is None or resolved == current_head:
        return None
    return resolved


def is_zero_sha(value: str) -> bool:
    return bool(value) and set(value) == {"0"}


def read_github_event() -> tuple[dict[str, object] | None, list[str]]:
    event_path = os.environ.get("GITHUB_EVENT_PATH")
    if not event_path:
        return None, []
    try:
        with Path(event_path).open("r", encoding="utf-8-sig") as handle:
            payload = json.load(handle)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        return None, [f"Unable to read GitHub event payload {event_path}: {error}"]
    if not isinstance(payload, dict):
        return None, [f"GitHub event payload must be a JSON object: {event_path}"]
    return payload, []


def resolve_base_ref() -> tuple[str | None, list[str]]:
    if BASE_REF:
        resolved = resolve_commit(BASE_REF)
        if resolved is None:
            return None, [f"Unable to resolve test baseline commit: {BASE_REF}"]
        return resolved, []

    event_name = os.environ.get("GITHUB_EVENT_NAME", "")
    github_actions = os.environ.get("GITHUB_ACTIONS") == "true"
    event, event_errors = read_github_event()
    if event_errors:
        return None, event_errors

    pull_request = event.get("pull_request") if event else None
    if isinstance(pull_request, dict) or event_name == "pull_request":
        base = pull_request.get("base") if isinstance(pull_request, dict) else None
        base_sha = base.get("sha") if isinstance(base, dict) else None
        if isinstance(base_sha, str) and base_sha and not is_zero_sha(base_sha):
            resolved = resolve_ci_baseline(base_sha)
            if resolved is not None:
                return resolved, []

        github_base_ref = os.environ.get("GITHUB_BASE_REF")
        base_name = base.get("ref") if isinstance(base, dict) else None
        candidate_name = github_base_ref or (base_name if isinstance(base_name, str) else "")
        if candidate_name:
            resolved = resolve_ci_baseline(f"origin/{candidate_name}")
            if resolved is not None:
                return resolved, []
        return None, [
            "Unable to resolve a trustworthy baseline commit for GitHub pull request event"
        ]

    before = event.get("before") if event else None
    if event_name == "push" or before is not None:
        if isinstance(before, str) and before and not is_zero_sha(before):
            resolved = resolve_ci_baseline(before)
            if resolved is not None:
                return resolved, []
        return None, [
            "Unable to resolve a trustworthy baseline commit for GitHub push event"
        ]

    if github_actions:
        if event_name in {"schedule", "workflow_dispatch"}:
            resolved = resolve_ci_baseline("HEAD^")
            if resolved is not None:
                return resolved, []
            return None, [
                "Unable to resolve HEAD^ as the baseline for scheduled or release checks"
            ]
        return None, [
            f"Unable to resolve a trustworthy baseline commit for GitHub event: "
            f"{event_name or 'unknown'}"
        ]

    branch_result = run_git("symbolic-ref", "--quiet", "--short", "HEAD")
    branch = branch_result.stdout.strip() if branch_result.returncode == 0 else ""
    if branch and branch != "dev":
        resolved = resolve_commit("origin/dev")
        if resolved is not None:
            return resolved, []

    resolved = resolve_commit("HEAD^")
    if resolved is not None:
        return resolved, []
    return None, [
        "Unable to resolve a trustworthy baseline commit; fetch origin/dev or run from "
        "a repository with a previous commit"
    ]


def load_baseline() -> tuple[
    dict[str, AllowlistEntry] | None,
    Callable[[str], str | None] | None,
    list[str],
]:
    if BASE_ALLOWLIST_PATH:
        entries, errors = load_allowlist(Path(BASE_ALLOWLIST_PATH))
        base_root = Path(BASE_ROOT_PATH) if BASE_ROOT_PATH else Path(".")

        def read_base_file(path: str) -> str | None:
            candidate = base_root / path
            if not candidate.is_file():
                return None
            return read_utf8_source(candidate)

        return entries, read_base_file, errors

    base_ref, base_ref_errors = resolve_base_ref()
    if base_ref is None:
        return None, None, base_ref_errors
    allowlist_result = run_git("show", f"{base_ref}:{repo_path(ALLOWLIST_PATH)}")
    if allowlist_result.returncode != 0:
        tree_result = run_git(
            "ls-tree",
            "--name-only",
            base_ref,
            "--",
            repo_path(ALLOWLIST_PATH),
        )
        if tree_result.returncode != 0:
            return None, None, [
                f"Unable to inspect baseline allowlist at {base_ref}: {ALLOWLIST_PATH}"
            ]
        if tree_result.stdout.strip():
            return None, None, [
                f"Unable to read baseline allowlist from {base_ref}: {ALLOWLIST_PATH}"
            ]
        entries = {}
        errors = []
    else:
        entries, errors = load_allowlist_text(
            allowlist_result.stdout,
            f"{base_ref}:{repo_path(ALLOWLIST_PATH)}",
        )

    def read_base_file(path: str) -> str | None:
        result = run_git("show", f"{base_ref}:{path}")
        return result.stdout if result.returncode == 0 else None

    return entries, read_base_file, errors


def baseline_violations(
    current: dict[str, AllowlistEntry],
    baseline: dict[str, AllowlistEntry] | None,
    read_base_file: Callable[[str], str | None] | None,
) -> list[str]:
    if baseline is None or read_base_file is None:
        return []
    violations: list[str] = []
    for path, entry in current.items():
        previous = baseline.get(path)
        if previous is not None:
            if entry.max_lines > previous.max_lines:
                violations.append(
                    f"{path}: allowlist budget increased from {previous.max_lines} "
                    f"to {entry.max_lines}; budgets may only stay fixed or shrink"
                )
            continue

        base_text = read_base_file(path)
        if base_text is None or not is_coverage_artifact(Path(path), base_text):
            violations.append(
                f"{path}: new allowlist entries are not allowed for artifacts "
                "that did not already exist on the base branch"
            )
            continue
        base_lines = len(base_text.splitlines()) if base_text else 0
        if entry.max_lines > base_lines:
            violations.append(
                f"{path}: newly registered historical artifact budget {entry.max_lines} "
                f"exceeds its base-branch size {base_lines}"
            )
    return violations


def main() -> int:
    try:
        allowlist, allowlist_errors = load_allowlist(ALLOWLIST_PATH)
        baseline, read_base_file, baseline_errors = load_baseline()
    except SourceScanError as error:
        print("Test coverage-file naming guard failed.")
        print(f"  Unable to scan C# declarations safely: {error}")
        return 1
    allowlist_errors.extend(baseline_errors)
    if allowlist_errors:
        print("Test coverage-file guard allowlist is invalid:")
        for error in allowlist_errors:
            print(f"  {error}")
        return 1

    try:
        violations = baseline_violations(allowlist, baseline, read_base_file)
        coverage_artifacts = iter_coverage_artifacts(ROOT)
        seen: set[str] = set()
        for path in coverage_artifacts:
            relative = repo_path(path)
            seen.add(relative)
            line_count = count_lines(path)
            entry = allowlist.get(relative)
            if entry is None:
                violations.append(
                    f"{relative}:{line_count}: new coverage-named test files or classes "
                    "are not allowed; name the test after behavior instead"
                )
                continue

            if line_count > entry.max_lines:
                violations.append(
                    f"{relative}:{line_count}: exceeds frozen coverage-test budget "
                    f"{entry.max_lines}; split or rename before growing"
                )
    except SourceScanError as error:
        print("Test coverage-file naming guard failed.")
        print(f"  Unable to scan C# declarations safely: {error}")
        return 1

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
            "Avoid generic coverage-named test files or classes. Use behavior-focused names, "
            "or shrink an existing historical file before changing its budget."
        )
        return 1

    print("test_coverage_file_guard: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
PY
