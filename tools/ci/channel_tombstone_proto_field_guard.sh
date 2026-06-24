#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# RFC §14.1 Layer 1 guard #4: every tombstone-capable Channel catalog / bot /
# device registration proto must carry a tombstone marker field.
#
# Two naming conventions are accepted:
#   - `bool is_deleted` — used by the dotted Aevatar.GAgents.Channel.*
#     abstractions tree (agents/Aevatar.GAgents.Channel.Runtime/protos/**).
#   - `bool tombstoned` — used by the per-package proto trees split out of
#     the original ChannelRuntime megamodule (Aevatar.GAgents.{Channel.Runtime,
#     Scheduled,Device}) following Channel RFC §7.1.1 projector watermark
#     coordination. Implementations on that side also carry
#     `int64 tombstone_state_version`, but only the boolean is enforced here.
#
# Scope: every .proto file under agents/. Issue #265 item 1.4 enforces the
# tombstone contract without carve-outs.
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
TOMBSTONE_FIELD_PATTERN = re.compile(r"\bbool\s+(is_deleted|tombstoned)\s*=\s*\d+\s*;")

root = Path("agents")
if not root.is_dir():
    print("channel_tombstone_proto_field_guard: ok (no agents/ tree)")
    sys.exit(0)

violations: list[str] = []

proto_files = list(root.rglob("*.proto"))

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
        if not TOMBSTONE_FIELD_PATTERN.search(body):
            violations.append(
                f"{path}:{message_name}: missing 'bool is_deleted' or 'bool tombstoned' tombstone field")

if violations:
    for violation in violations:
        print(violation)
    print("channel_tombstone_proto_field_guard: a tombstone proto field ('bool is_deleted' or 'bool tombstoned') is required.")
    sys.exit(1)

print("channel_tombstone_proto_field_guard: ok")
PY
