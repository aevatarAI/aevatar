#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

python3 <<'PY'
from pathlib import Path
import re
import sys

root = Path("agents/Aevatar.GAgents.Channel.Abstractions")
files = [path for path in root.rglob("*.cs") if "obj" not in path.parts and "bin" not in path.parts]
pattern = re.compile(r"\binterface\s+([A-Za-z_][A-Za-z0-9_]*)\s*([^{}]*)\{", re.MULTILINE)

violations: list[str] = []

for path in files:
    text = path.read_text(encoding="utf-8")
    for match in pattern.finditer(text):
        name = match.group(1)
        declaration_suffix = match.group(2) or ""
        body_start = match.end() - 1
        depth = 0
        body_end = None

        for index in range(body_start, len(text)):
            char = text[index]
            if char == "{":
                depth += 1
            elif char == "}":
                depth -= 1
                if depth == 0:
                    body_end = index
                    break

        if body_end is None:
            continue

        body = text[body_start + 1:body_end]
        has_runtime_surface = any(token in body for token in ("InitializeAsync(", "StartReceivingAsync(", "StopReceivingAsync("))
        has_outbound_surface = any(token in body for token in ("SendAsync(", "UpdateAsync(", "DeleteAsync(", "ContinueConversationAsync("))
        inherits_both = "IChannelTransport" in declaration_suffix and "IChannelOutboundPort" in declaration_suffix

        if (has_runtime_surface and has_outbound_surface) or inherits_both:
            violations.append(f"{path}:{name}")

if violations:
    print("Channel mega-interface regression detected:")
    for violation in violations:
        print(f"  {violation}")
    sys.exit(1)

print("channel_mega_interface_guard: ok")
PY
