#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
SCAN_ROOT="${AEVATAR_NYXID_CHAT_GUARD_ROOT:-${REPO_ROOT}}"

nyxid_dir="${SCAN_ROOT}/agents/Aevatar.GAgents.NyxidChat"
task_proto="${nyxid_dir}/protos/nyxid_chat_task.proto"
readmodel_proto="${SCAN_ROOT}/src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto"

required_files=(
  "${task_proto}"
  "${readmodel_proto}"
  "${nyxid_dir}/NyxIdChatConversationGAgent.cs"
  "${nyxid_dir}/NyxIdChatTaskLifecycle.cs"
  "${nyxid_dir}/NyxIdChatTaskTransitionPolicy.cs"
  "${nyxid_dir}/NyxIdAssistantActionRegistry.cs"
)

for required_file in "${required_files[@]}"; do
  if [[ ! -f "${required_file}" ]]; then
    echo "Missing NyxIdChat semantics guard anchor: ${required_file#"${SCAN_ROOT}/"}" >&2
    exit 1
  fi
done

python3 - "${SCAN_ROOT}" <<'PY'
from __future__ import annotations

import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
nyxid = root / "agents" / "Aevatar.GAgents.NyxidChat"
task_proto = nyxid / "protos" / "nyxid_chat_task.proto"
readmodel_proto = (
    root
    / "src"
    / "Aevatar.Studio.Projection"
    / "ReadModels"
    / "studio_projection_readmodels.proto"
)


def relative(path: pathlib.Path) -> str:
    return path.relative_to(root).as_posix()


def strip_comments(text: str) -> str:
    """Remove C-style comments while preserving quoted strings and line numbers."""
    output: list[str] = []
    index = 0
    state = "code"
    quote = ""
    while index < len(text):
        current = text[index]
        following = text[index + 1] if index + 1 < len(text) else ""

        if state == "code":
            if current == "/" and following == "/":
                output.extend((" ", " "))
                index += 2
                state = "line_comment"
                continue
            if current == "/" and following == "*":
                output.extend((" ", " "))
                index += 2
                state = "block_comment"
                continue
            if current in {'"', "'"}:
                quote = current
                state = "string"
            output.append(current)
            index += 1
            continue

        if state == "string":
            output.append(current)
            if current == "\\" and index + 1 < len(text):
                output.append(text[index + 1])
                index += 2
                continue
            if current == quote:
                state = "code"
            index += 1
            continue

        if state == "line_comment":
            if current == "\n":
                output.append(current)
                state = "code"
            else:
                output.append(" ")
            index += 1
            continue

        if current == "*" and following == "/":
            output.extend((" ", " "))
            index += 2
            state = "code"
            continue
        output.append("\n" if current == "\n" else " ")
        index += 1

    return "".join(output)


def line_number(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


violations: list[tuple[str, str, int, str]] = []

# Actor-owned facts must not migrate into service fields. The declaration must
# be a mutable collection field and its name must carry operation/action/
# cancellation semantics. Method-local temporary collections never match the
# required member visibility prefix.
collection_field = re.compile(
    r"(?im)^[ \t]*(?:private|protected|internal|public)[ \t]+"
    r"(?:(?:static|readonly|volatile)[ \t]+)*"
    r"(?:(?:global::)?System\.Collections\.(?:Concurrent\.)?)?"
    r"(?:ConcurrentDictionary|Dictionary|HashSet|Queue)[ \t]*<[^;\n]+>"
    r"[ \t]+(?P<name>_?[A-Za-z0-9_]*(?:operation|action|cancellation)[A-Za-z0-9_]*)"
    r"[ \t]*(?:=|;)",
)
for path in sorted(nyxid.rglob("*.cs")):
    if any(part in {"bin", "obj"} for part in path.parts) or path.name.endswith(".g.cs"):
        continue
    if path.name.endswith("GAgent.cs"):
        continue
    text = strip_comments(path.read_text(encoding="utf-8"))
    for match in collection_field.finditer(text):
        violations.append(
            (
                "service-level operation/action/cancellation collections",
                relative(path),
                line_number(text, match.start()),
                match.group("name"),
            )
        )

# Controller/task/control decisions must consume typed receipts/signals. JSON
# schema parsing remains legal in the external action registry and adapters.
decision_files = (
    "NyxIdChatConversationGAgent.cs",
    "NyxIdChatTurnGAgent.cs",
    "NyxIdChatTaskLifecycle.cs",
    "NyxIdChatTaskTransitionPolicy.cs",
    "NyxIdChatControlCommands.cs",
    "NyxIdChatBrowserActions.cs",
)
json_error_inference = re.compile(
    r"(?i)(?:TryGetProperty|GetProperty)[ \t\r\n]*\([ \t\r\n]*\"error\""
    r"|\[[ \t\r\n]*\"error\"[ \t\r\n]*\]"
)
for file_name in decision_files:
    path = nyxid / file_name
    if not path.exists():
        continue
    text = strip_comments(path.read_text(encoding="utf-8"))
    for match in json_error_inference.finditer(text):
        violations.append(
            (
                'generic JSON "error" inference',
                relative(path),
                line_number(text, match.start()),
                match.group(0).replace("\n", " ").strip(),
            )
        )

# Aevatar never asks the browser to submit an OAuth/device user code. Only the
# production action registry and protobuf action contract are scanned, so
# explicit negative tests and design documentation remain free to name it.
forbidden_action = re.compile(r"(?i)device\.approve\.user_code|device_approve_user_code")
for path in (nyxid / "NyxIdAssistantActionRegistry.cs", task_proto):
    text = strip_comments(path.read_text(encoding="utf-8"))
    for match in forbidden_action.finditer(text):
        violations.append(
            (
                "device.approve.user_code",
                relative(path),
                line_number(text, match.start()),
                match.group(0),
            )
        )

forbidden_secret_fields = {
    "access_token",
    "refresh_token",
    "bearer_token",
    "oauth_token",
    "authorization",
    "authorization_header",
    "authorization_headers",
    "cookie",
    "cookies",
    "cookie_header",
    "cookie_headers",
    "client_secret",
    "user_code",
    "device_code",
    "password",
    "passphrase",
    "secret",
    "secret_value",
    "token",
    "token_value",
    "raw_body",
    "raw_upstream_body",
    "credential",
    "credential_value",
    "credentials",
    "uri_userinfo",
}
proto_field = re.compile(
    r"(?m)^[ \t]*(?:optional[ \t]+|repeated[ \t]+)?"
    r"(?:map[ \t]*<[^>]+>|[.A-Za-z_][.A-Za-z0-9_]*)"
    r"[ \t]+(?P<name>[a-z][a-z0-9_]*)[ \t]*=[ \t]*\d+[ \t]*;"
)


def scan_secret_fields(path: pathlib.Path, text: str, label: str) -> None:
    for match in proto_field.finditer(text):
        name = match.group("name")
        if name in forbidden_secret_fields:
            violations.append(
                (
                    label,
                    relative(path),
                    line_number(text, match.start()),
                    name,
                )
            )


task_text = strip_comments(task_proto.read_text(encoding="utf-8"))
scan_secret_fields(task_proto, task_text, "secret-bearing protobuf fields")

# studio_projection_readmodels.proto contains other bounded adapters. Only
# NyxIdChat conversation document messages belong to this semantic contract.
readmodel_text = strip_comments(readmodel_proto.read_text(encoding="utf-8"))
message_start = re.compile(r"\bmessage[ \t]+NyxIdChatConversation[A-Za-z0-9_]*[ \t]*\{")
for message in message_start.finditer(readmodel_text):
    depth = 0
    end = None
    for index in range(message.end() - 1, len(readmodel_text)):
        if readmodel_text[index] == "{":
            depth += 1
        elif readmodel_text[index] == "}":
            depth -= 1
            if depth == 0:
                end = index + 1
                break
    if end is None:
        violations.append(
            (
                "secret-bearing read-model fields",
                relative(readmodel_proto),
                line_number(readmodel_text, message.start()),
                "unterminated NyxIdChatConversation message",
            )
        )
        continue
    block = readmodel_text[message.start() : end]
    for field in proto_field.finditer(block):
        name = field.group("name")
        if name in forbidden_secret_fields:
            violations.append(
                (
                    "secret-bearing read-model fields",
                    relative(readmodel_proto),
                    line_number(readmodel_text, message.start() + field.start()),
                    name,
                )
            )

if violations:
    for category, path, line, detail in violations:
        print(f"{path}:{line}: {category}: {detail}")
    print(
        "NyxIdChat durable/control semantics must remain actor-owned, typed, "
        "device-code-free, and secret-free."
    )
    raise SystemExit(1)
PY

echo "NyxIdChat semantics guard passed."
