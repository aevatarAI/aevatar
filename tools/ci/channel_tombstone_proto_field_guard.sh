#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# RFC §14.1 Layer 1 guard #4: every tombstone-capable Channel catalog / bot /
# device registration proto must carry `bool is_deleted`.
#
# Scope: proto files under agents/Aevatar.GAgents.Channel.*/** (the Channel
# RFC abstraction tree with the dotted namespace). Legacy proto directories
# (agents/Aevatar.GAgents.ChannelRuntime/** etc.) are excluded — their
# tombstone migration is tracked separately.
#
# Messages checked:
#   UserAgentCatalogEntry
#   ChannelBotRegistrationEntry
#   DeviceRegistrationEntry

python3 <<'PY'
from pathlib import Path
import re
import sys

MESSAGES = ("UserAgentCatalogEntry", "ChannelBotRegistrationEntry", "DeviceRegistrationEntry")
MESSAGE_PATTERN = re.compile(r"^\s*message\s+(\w+)\s*\{", re.MULTILINE)

root = Path("agents")
if not root.is_dir():
    print("channel_tombstone_proto_field_guard: ok (no agents/ tree)")
    sys.exit(0)

violations: list[str] = []

proto_files = [
    path
    for path in root.rglob("*.proto")
    if any(part.startswith("Aevatar.GAgents.Channel.") for part in path.parts)
]

for path in proto_files:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as error:
        violations.append(f"{path}: cannot read ({error})")
        continue

    for match in MESSAGE_PATTERN.finditer(text):
        message_name = match.group(1)
        if message_name not in MESSAGES:
            continue

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
            violations.append(f"{path}:{message_name}: unterminated message body")
            continue

        body = text[body_start + 1:body_end]
        if not re.search(r"\bbool\s+is_deleted\s*=\s*\d+\s*;", body):
            violations.append(f"{path}:{message_name}: missing 'bool is_deleted' tombstone field")

if violations:
    for violation in violations:
        print(violation)
    print("channel_tombstone_proto_field_guard: tombstone proto field 'bool is_deleted' is required.")
    sys.exit(1)

print("channel_tombstone_proto_field_guard: ok")
PY
