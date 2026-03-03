#!/usr/bin/env bash
set -euo pipefail

# One-command bootstrap for OpenClaw connectors in ~/.aevatar/connectors.json.
# - Auto-detects gateway base URL and token when possible.
# - Upserts OpenClaw connectors without deleting unrelated connectors.
# - Creates a timestamped backup by default.

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required but not found in PATH." >&2
  exit 1
fi

AEVATAR_HOME="${AEVATAR_HOME:-$HOME/.aevatar}"
CONNECTORS_PATH="${AEVATAR_CONNECTORS_PATH:-$AEVATAR_HOME/connectors.json}"
OPENCLAW_CONFIG_PATH="${OPENCLAW_CONFIG_PATH:-$HOME/.openclaw/openclaw.json}"

python3 - "$CONNECTORS_PATH" "$OPENCLAW_CONFIG_PATH" <<'PY'
import copy
import datetime as dt
import json
import os
import pathlib
import shutil
import subprocess
import sys
from typing import Any, Optional


CONNECTORS_PATH = pathlib.Path(sys.argv[1]).expanduser()
OPENCLAW_CONFIG_PATH = pathlib.Path(sys.argv[2]).expanduser()


def env_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "y", "on"}


def normalize_http_base_url(value: str) -> str:
    normalized = value.strip()
    if not normalized:
        return normalized
    if "://" not in normalized:
        normalized = f"http://{normalized}"
    return normalized.rstrip("/")


def try_load_json(path: pathlib.Path) -> Any:
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return None


def load_existing_root(path: pathlib.Path) -> dict[str, Any]:
    if not path.exists():
        return {}
    try:
        root = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as ex:
        raise SystemExit(
            f"Existing connectors file is invalid JSON: {path}\n"
            f"Error: {ex}\n"
            "Fix the file first (or move it away), then re-run this script."
        )
    if not isinstance(root, dict):
        raise SystemExit(
            f"Existing connectors file root must be a JSON object: {path}"
        )
    return root


def flatten_connectors(root: dict[str, Any]) -> list[dict[str, Any]]:
    node = root.get("connectors")
    if isinstance(node, list):
        source_items = node
    elif isinstance(node, dict):
        defs = node.get("definitions")
        if isinstance(defs, list):
            source_items = defs
        else:
            source_items = []
            for key, value in node.items():
                if key == "definitions":
                    continue
                if isinstance(value, dict):
                    item = dict(value)
                    if not item.get("name"):
                        item["name"] = key
                    source_items.append(item)
    else:
        return []

    result: list[dict[str, Any]] = []
    for item in source_items:
        if not isinstance(item, dict):
            continue
        name = item.get("name")
        if isinstance(name, str) and name.strip():
            result.append(copy.deepcopy(item))
    return result


def detect_gateway_base_url() -> tuple[str, str]:
    env_url = os.getenv("OPENCLAW_GATEWAY_BASE_URL", "").strip()
    if env_url:
        return normalize_http_base_url(env_url), "OPENCLAW_GATEWAY_BASE_URL"

    env_port = os.getenv("OPENCLAW_GATEWAY_PORT", "").strip()
    if env_port.isdigit():
        return f"http://127.0.0.1:{int(env_port)}", "OPENCLAW_GATEWAY_PORT"

    if shutil.which("openclaw"):
        try:
            proc = subprocess.run(
                ["openclaw", "gateway", "status", "--json"],
                check=False,
                capture_output=True,
                text=True,
                timeout=8,
            )
            if proc.returncode == 0 and proc.stdout.strip():
                doc = json.loads(proc.stdout)
                if isinstance(doc, dict):
                    gateway = doc.get("gateway")
                    if isinstance(gateway, dict):
                        host = gateway.get("bindHost")
                        port = gateway.get("port")
                        if isinstance(port, int) and port > 0:
                            resolved_host = (
                                host.strip()
                                if isinstance(host, str) and host.strip()
                                else "127.0.0.1"
                            )
                            return (
                                f"http://{resolved_host}:{port}",
                                "openclaw gateway status --json",
                            )
        except Exception:
            pass

    cfg = try_load_json(OPENCLAW_CONFIG_PATH)
    if isinstance(cfg, dict):
        gateway = cfg.get("gateway")
        if isinstance(gateway, dict):
            remote = gateway.get("remote")
            if isinstance(remote, dict):
                remote_url = remote.get("url")
                if isinstance(remote_url, str) and remote_url.strip():
                    url = remote_url.strip()
                    if url.startswith("ws://"):
                        url = "http://" + url[len("ws://"):]
                    elif url.startswith("wss://"):
                        url = "https://" + url[len("wss://"):]
                    return (
                        normalize_http_base_url(url),
                        f"{OPENCLAW_CONFIG_PATH}:gateway.remote.url",
                    )
            cfg_port = gateway.get("port")
            if isinstance(cfg_port, int) and cfg_port > 0:
                return (
                    f"http://127.0.0.1:{cfg_port}",
                    f"{OPENCLAW_CONFIG_PATH}:gateway.port",
                )

    return "http://127.0.0.1:18789", "fallback-default"


def detect_gateway_token() -> tuple[str, str]:
    env_token = os.getenv("OPENCLAW_GATEWAY_TOKEN", "").strip()
    if env_token:
        return env_token, "OPENCLAW_GATEWAY_TOKEN"

    cfg = try_load_json(OPENCLAW_CONFIG_PATH)
    if isinstance(cfg, dict):
        gateway = cfg.get("gateway")
        if isinstance(gateway, dict):
            auth = gateway.get("auth")
            if isinstance(auth, dict):
                token = auth.get("token")
                if isinstance(token, str) and token.strip():
                    return (
                        token.strip(),
                        f"{OPENCLAW_CONFIG_PATH}:gateway.auth.token",
                    )

    return "", "not-detected"


def detect_screenshot_output_dir() -> str:
    configured = os.getenv("OPENCLAW_SCREENSHOT_OUTPUT_DIR", "").strip()
    if configured:
        return str(pathlib.Path(configured).expanduser())
    return str((CONNECTORS_PATH.parent / "screenshot").expanduser())


def build_openclaw_connectors(
    base_url: str,
    token: str,
    chat_enabled: bool,
    screenshot_output_dir: str,
) -> list[dict[str, Any]]:
    default_headers: dict[str, str] = {
        "Content-Type": "application/json",
    }
    if token:
        default_headers["Authorization"] = f"Bearer {token}"

    media_save_script = (
        "import os,re,shutil,sys,time,pathlib;"
        "raw=sys.stdin.read();"
        "m=re.search(r'MEDIA:([^\\\\s]+)',raw);"
        "if not m: raise SystemExit('No MEDIA path found in screenshot output.');"
        "src=pathlib.Path(m.group(1)).expanduser();"
        "if not src.exists(): raise SystemExit(f'MEDIA file not found: {src}');"
        "out_dir=pathlib.Path(os.environ.get('AEVATAR_OPENCLAW_SCREENSHOT_DIR', '~/.aevatar/screenshot')).expanduser();"
        "out_dir.mkdir(parents=True,exist_ok=True);"
        "suffix=src.suffix if src.suffix else '.png';"
        "dest=out_dir/f\"shot-{time.strftime('%Y%m%d-%H%M%S')}{suffix}\";"
        "shutil.copy2(src,dest);"
        "print(str(dest));"
    )

    return [
        {
            "name": "openclaw_cli_gateway_status",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 15000,
            "cli": {
                "command": "openclaw",
                "fixedArguments": ["gateway", "status", "--json"],
                "allowedOperations": [],
                "allowedInputKeys": [],
            },
        },
        {
            "name": "openclaw_cli_gateway_health",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 15000,
            "cli": {
                "command": "openclaw",
                "fixedArguments": ["gateway", "call", "health", "--json"],
                "allowedOperations": [],
                "allowedInputKeys": [],
            },
        },
        {
            "name": "openclaw_cli_agent",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 20000,
            "cli": {
                "command": "openclaw",
                "fixedArguments": ["agent"],
                "allowedOperations": ["status", "run", "resume"],
                "allowedInputKeys": [],
            },
        },
        {
            "name": "openclaw_cli_browser_status",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 20000,
            "cli": {
                "command": "openclaw",
                "fixedArguments": ["browser", "status", "--json"],
                "allowedOperations": [],
                "allowedInputKeys": [],
            },
        },
        {
            "name": "openclaw_cli_browser_extension_install",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 20000,
            "cli": {
                "command": "openclaw",
                "fixedArguments": ["browser", "extension", "install"],
                "allowedOperations": [],
                "allowedInputKeys": [],
            },
        },
        {
            "name": "openclaw_cli_open_chrome_extensions",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 10000,
            "cli": {
                "command": "open",
                "fixedArguments": ["-a", "Google Chrome", "chrome://extensions"],
                "allowedOperations": [],
                "allowedInputKeys": [],
            },
        },
        {
            "name": "openclaw_cli_browser_open_example",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 30000,
            "cli": {
                "command": "openclaw",
                "fixedArguments": ["browser", "open", "https://example.com", "--json"],
                "allowedOperations": [],
                "allowedInputKeys": [],
            },
        },
        {
            "name": "openclaw_cli_browser_snapshot",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 30000,
            "cli": {
                "command": "openclaw",
                "fixedArguments": ["browser", "snapshot", "--json"],
                "allowedOperations": [],
                "allowedInputKeys": [],
            },
        },
        {
            "name": "openclaw_cli_browser_screenshot",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 30000,
            "cli": {
                "command": "openclaw",
                "fixedArguments": ["browser", "screenshot"],
                "allowedOperations": [],
                "allowedInputKeys": [],
            },
        },
        {
            "name": "openclaw_cli_media_save",
            "type": "cli",
            "enabled": True,
            "timeoutMs": 15000,
            "cli": {
                "command": "python3",
                "fixedArguments": ["-c", media_save_script],
                "allowedOperations": [],
                "allowedInputKeys": [],
                "environment": {
                    "AEVATAR_OPENCLAW_SCREENSHOT_DIR": screenshot_output_dir,
                },
            },
        },
        {
            "name": "openclaw_http_gateway_root",
            "type": "http",
            "enabled": True,
            "timeoutMs": 15000,
            "http": {
                "baseUrl": base_url,
                "allowedMethods": ["GET"],
                "allowedPaths": ["/"],
                "allowedInputKeys": [],
                "defaultHeaders": default_headers,
            },
        },
        {
            "name": "openclaw_http_tools",
            "type": "http",
            "enabled": True,
            "timeoutMs": 60000,
            "http": {
                "baseUrl": base_url,
                "allowedMethods": ["POST"],
                "allowedPaths": ["/tools/invoke"],
                "allowedInputKeys": [
                    "tool",
                    "arguments",
                    "session_id",
                    "channel_id",
                    "user_id",
                    "request_id",
                ],
                "defaultHeaders": default_headers,
            },
        },
        {
            "name": "openclaw_http_chat",
            "type": "http",
            "enabled": chat_enabled,
            "timeoutMs": 60000,
            "http": {
                "baseUrl": base_url,
                "allowedMethods": ["POST"],
                "allowedPaths": ["/v1/chat/completions"],
                "allowedInputKeys": [
                    "model",
                    "messages",
                    "temperature",
                    "max_tokens",
                ],
                "defaultHeaders": default_headers,
            },
        },
    ]


def merge_connectors(
    existing: list[dict[str, Any]],
    managed: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    managed_by_name = {
        item["name"]: item
        for item in managed
        if isinstance(item.get("name"), str) and item["name"].strip()
    }
    managed_order = [item["name"] for item in managed]

    merged: list[dict[str, Any]] = []
    seen: set[str] = set()
    for item in existing:
        name = item.get("name")
        if not isinstance(name, str):
            continue
        normalized = name.strip()
        if not normalized or normalized in seen:
            continue
        if normalized in managed_by_name:
            merged.append(copy.deepcopy(managed_by_name[normalized]))
        else:
            merged.append(copy.deepcopy(item))
        seen.add(normalized)

    for name in managed_order:
        if name not in seen:
            merged.append(copy.deepcopy(managed_by_name[name]))
            seen.add(name)
    return merged


def main() -> None:
    chat_enabled = env_bool("ENABLE_OPENCLAW_HTTP_CHAT", False)
    backup_enabled = env_bool("BACKUP_CONNECTORS_JSON", True)

    base_url, base_url_source = detect_gateway_base_url()
    token, token_source = detect_gateway_token()
    screenshot_output_dir = detect_screenshot_output_dir()

    root = load_existing_root(CONNECTORS_PATH)
    existing_connectors = flatten_connectors(root)
    managed_connectors = build_openclaw_connectors(
        base_url=base_url,
        token=token,
        chat_enabled=chat_enabled,
        screenshot_output_dir=screenshot_output_dir,
    )
    merged_connectors = merge_connectors(existing_connectors, managed_connectors)

    CONNECTORS_PATH.parent.mkdir(parents=True, exist_ok=True)

    backup_path: Optional[pathlib.Path] = None
    if CONNECTORS_PATH.exists() and backup_enabled:
        stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
        backup_path = CONNECTORS_PATH.with_name(f"{CONNECTORS_PATH.name}.bak.{stamp}")
        shutil.copy2(CONNECTORS_PATH, backup_path)

    root["connectors"] = merged_connectors
    CONNECTORS_PATH.write_text(
        json.dumps(root, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    print(f"Updated connectors: {CONNECTORS_PATH}")
    if backup_path is not None:
        print(f"Backup created: {backup_path}")
    print(f"Resolved gateway baseUrl: {base_url} ({base_url_source})")
    if token:
        print(f"Resolved gateway token: detected ({token_source})")
    else:
        print(
            "Resolved gateway token: not detected "
            "(set OPENCLAW_GATEWAY_TOKEN before running workflows)."
        )
    print("Upserted connectors:")
    print("  - openclaw_cli_gateway_status")
    print("  - openclaw_cli_gateway_health")
    print("  - openclaw_cli_agent")
    print("  - openclaw_cli_browser_status")
    print("  - openclaw_cli_browser_extension_install")
    print("  - openclaw_cli_open_chrome_extensions")
    print("  - openclaw_cli_browser_open_example")
    print("  - openclaw_cli_browser_snapshot")
    print("  - openclaw_cli_browser_screenshot")
    print(f"  - openclaw_cli_media_save (dest={screenshot_output_dir})")
    print("  - openclaw_http_gateway_root")
    print("  - openclaw_http_tools")
    print(
        "  - openclaw_http_chat "
        f"(enabled={str(chat_enabled).lower()}, set ENABLE_OPENCLAW_HTTP_CHAT=true to enable)"
    )


if __name__ == "__main__":
    main()
PY
