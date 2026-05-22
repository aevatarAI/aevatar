#!/usr/bin/env python3
"""Guard Nyx relay replay authority against process-local dictionaries."""

from __future__ import annotations

from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[2]
SCAN_ROOTS = [
    ROOT / "agents" / "Aevatar.GAgents.NyxidChat",
    ROOT / "agents" / "channels" / "Aevatar.GAgents.Channel.NyxIdRelay",
]
FORBIDDEN_TYPE_NAMES = (
    "INyxIdRelayReplayGuard",
    "NyxIdRelayReplayGuard",
    "INyxRelayBridgeIdempotencyGuard",
    "NyxRelayBridgeIdempotencyGuard",
)
CONCURRENT_DICTIONARY = re.compile(r"\bConcurrentDictionary\s*<")


def is_ignored(path: Path, explicit: bool) -> bool:
    parts = set(path.parts)
    if path.suffix != ".cs":
        return True
    return not explicit and ("bin" in parts or "obj" in parts)


def iter_scan_files() -> list[Path]:
    if len(sys.argv) > 1:
        return [Path(arg).resolve() for arg in sys.argv[1:]]

    files: list[Path] = []
    for root in SCAN_ROOTS:
        if not root.exists():
            continue
        files.extend(root.rglob("*.cs"))
    return files


def main() -> int:
    violations: list[str] = []
    explicit = len(sys.argv) > 1
    for path in iter_scan_files():
        if is_ignored(path, explicit):
            continue
        relative = path.relative_to(ROOT) if path.is_relative_to(ROOT) else path
        text = path.read_text(encoding="utf-8")
        for line_no, line in enumerate(text.splitlines(), start=1):
            stripped = line.strip()
            if stripped.startswith("//") and "Old pattern:" in stripped:
                continue
            if CONCURRENT_DICTIONARY.search(line) and re.search(
                r"replay|idempot|claim|callback", line, re.IGNORECASE
            ):
                violations.append(f"{relative}:{line_no}:{line}")
            for name in FORBIDDEN_TYPE_NAMES:
                if name in line and "Old pattern:" not in line:
                    violations.append(f"{relative}:{line_no}:{line}")

    if violations:
        print("\n".join(violations))
        print(
            "Nyx relay replay/idempotency authority must be actor-owned typed state, "
            "not process-local guards or ConcurrentDictionary claims.",
            file=sys.stderr,
        )
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
