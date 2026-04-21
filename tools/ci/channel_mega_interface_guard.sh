#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

python3 <<'PY'
import os
from pathlib import Path
import re
import sys

root = Path(os.environ.get("CHANNEL_MEGA_INTERFACE_GUARD_ROOT", "agents/Aevatar.GAgents.Channel.Abstractions"))
files = [path for path in root.rglob("*.cs") if "obj" not in path.parts and "bin" not in path.parts]
pattern = re.compile(r"\binterface\s+([A-Za-z_][A-Za-z0-9_]*)\s*([^{}]*)\{", re.MULTILINE)

surfaces: dict[str, dict[str, object]] = {}

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
        surface = surfaces.setdefault(
            name,
            {
                "paths": set(),
                "has_runtime_surface": False,
                "has_outbound_surface": False,
                "inherits_transport": False,
                "inherits_outbound": False,
            },
        )
        surface["paths"].add(str(path))
        surface["has_runtime_surface"] = surface["has_runtime_surface"] or any(
            token in body for token in ("InitializeAsync(", "StartReceivingAsync(", "StopReceivingAsync(")
        )
        surface["has_outbound_surface"] = surface["has_outbound_surface"] or any(
            token in body for token in ("SendAsync(", "UpdateAsync(", "DeleteAsync(", "ContinueConversationAsync(")
        )
        surface["inherits_transport"] = surface["inherits_transport"] or "IChannelTransport" in declaration_suffix
        surface["inherits_outbound"] = surface["inherits_outbound"] or "IChannelOutboundPort" in declaration_suffix

violations: list[str] = []
for name, surface in sorted(surfaces.items()):
    exposes_runtime_surface = bool(surface["has_runtime_surface"] or surface["inherits_transport"])
    exposes_outbound_surface = bool(surface["has_outbound_surface"] or surface["inherits_outbound"])
    if exposes_runtime_surface and exposes_outbound_surface:
        paths = ", ".join(sorted(surface["paths"]))
        violations.append(f"{name} ({paths})")

if violations:
    print("Channel mega-interface regression detected:")
    for violation in violations:
        print(f"  {violation}")
    sys.exit(1)

print("channel_mega_interface_guard: ok")
PY
