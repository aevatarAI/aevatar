#!/usr/bin/env python3
"""
Migrate legacy Aevatar workflow YAML files to the current schema.

Target schema:
  - top level: name / description / configuration / roles / steps
  - step keys: id / type / target_role / parameters / next / children / branches / retry / on_error / timeout_ms

Legacy keys (e.g. inputs/defaults/output/agent/store/registers/step/if_true/if_false) are preserved
under step.parameters where possible, so files can be parsed by the current WorkflowParser.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import shutil
import sys
from pathlib import Path
from typing import Any

try:
    import yaml
except Exception as exc:  # pragma: no cover - runtime guard
    print(f"[error] PyYAML is required: {exc}", file=sys.stderr)
    sys.exit(2)


STEP_STRUCTURAL_KEYS = {
    "id",
    "type",
    "target_role",
    "role",
    "agent",
    "parameters",
    "next",
    "children",
    "steps",
    "step",
    "branches",
    "retry",
    "on_error",
    "timeout_ms",
}

ROLE_ALLOWED_KEYS = {
    "id",
    "name",
    "system_prompt",
    "provider",
    "model",
    "temperature",
    "max_tokens",
    "max_tool_rounds",
    "max_history_messages",
    "stream_buffer_capacity",
    "event_modules",
    "event_routes",
    "extensions",
    "connectors",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Migrate ~/.aevatar/workflows legacy YAML files to current schema.",
    )
    parser.add_argument(
        "--workflows-dir",
        default=str(Path.home() / ".aevatar" / "workflows"),
        help="Workflow directory to migrate (default: ~/.aevatar/workflows).",
    )
    parser.add_argument(
        "--pattern",
        default="*.yaml",
        help="Glob pattern for workflow files (default: *.yaml).",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Preview migration results without writing files.",
    )
    parser.add_argument(
        "--backup-dir",
        default="",
        help="Backup directory. Default: <workflows-dir>/.legacy-backup-<timestamp>.",
    )
    return parser.parse_args()


def to_string(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return str(value)
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def parse_int_like(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        if value.is_integer():
            return int(value)
        return None
    if isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        if text.isdigit() or (text.startswith("-") and text[1:].isdigit()):
            return int(text)
    return None


def parse_float_like(value: Any) -> float | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        try:
            return float(text)
        except ValueError:
            return None
    return None


def title_from_role_id(role_id: str) -> str:
    parts = [p for p in re.split(r"[_\-\s]+", role_id.strip()) if p]
    if not parts:
        return role_id
    return " ".join(part.capitalize() for part in parts)


def read_mapping_value(mapping: dict[str, Any], *keys: str) -> Any:
    lower_map = {str(k).lower(): v for k, v in mapping.items()}
    for key in keys:
        if key.lower() in lower_map:
            return lower_map[key.lower()]
    return None


def normalize_branches(raw: Any) -> dict[str, str] | None:
    out: dict[str, str] = {}
    if isinstance(raw, dict):
        for key, value in raw.items():
            branch_key = to_string(key).strip()
            if not branch_key:
                continue
            target = ""
            if isinstance(value, dict):
                target = to_string(read_mapping_value(value, "next", "to", "target", "step")).strip()
            else:
                target = to_string(value).strip()
            if target:
                out[branch_key] = target
    elif isinstance(raw, list):
        for item in raw:
            if not isinstance(item, dict):
                continue
            branch_key = to_string(read_mapping_value(item, "condition", "when", "case", "label", "if")).strip()
            target = to_string(read_mapping_value(item, "next", "to", "target", "step")).strip()
            if branch_key and target:
                out[branch_key] = target
    return out or None


def normalize_retry(raw: Any) -> dict[str, Any] | None:
    if not isinstance(raw, dict):
        return None
    out: dict[str, Any] = {}
    max_attempts = parse_int_like(raw.get("max_attempts"))
    if max_attempts is not None:
        out["max_attempts"] = max_attempts
    backoff = to_string(raw.get("backoff")).strip()
    if backoff:
        out["backoff"] = backoff
    delay_ms = parse_int_like(raw.get("delay_ms"))
    if delay_ms is not None:
        out["delay_ms"] = delay_ms
    return out or None


def normalize_on_error(raw: Any) -> dict[str, Any] | None:
    if not isinstance(raw, dict):
        return None
    out: dict[str, Any] = {}
    strategy = to_string(raw.get("strategy")).strip()
    if strategy:
        out["strategy"] = strategy
    fallback_step = to_string(raw.get("fallback_step")).strip()
    if fallback_step:
        out["fallback_step"] = fallback_step
    default_output = to_string(raw.get("default_output")).strip()
    if default_output:
        out["default_output"] = default_output
    return out or None


def collect_role_ids_from_step(step: Any, out: set[str]) -> None:
    if not isinstance(step, dict):
        return

    for key in ("agent", "target_role", "role"):
        value = step.get(key)
        if isinstance(value, str) and value.strip():
            out.add(value.strip())

    for child_key in ("children", "steps", "if_true", "if_false"):
        children = step.get(child_key)
        if isinstance(children, list):
            for child in children:
                collect_role_ids_from_step(child, out)

    one_child = step.get("step")
    if isinstance(one_child, dict):
        collect_role_ids_from_step(one_child, out)

    generator = step.get("generator")
    if isinstance(generator, dict):
        collect_role_ids_from_step(generator, out)


def normalize_role_entry(raw_role: Any) -> dict[str, Any] | None:
    if not isinstance(raw_role, dict):
        return None

    role_id = to_string(raw_role.get("id")).strip()
    role_name = to_string(raw_role.get("name")).strip()
    if not role_id and not role_name:
        return None
    if not role_id:
        role_id = role_name
    if not role_name:
        role_name = title_from_role_id(role_id)

    out: dict[str, Any] = {"id": role_id, "name": role_name}
    for key in ROLE_ALLOWED_KEYS:
        if key in ("id", "name"):
            continue
        if key not in raw_role:
            continue
        value = raw_role[key]
        if value is None:
            continue

        if key in {"provider", "model", "system_prompt", "event_modules", "event_routes"}:
            text = to_string(value).strip()
            if text:
                out[key] = text
            continue

        if key in {"max_tokens", "max_tool_rounds", "max_history_messages", "stream_buffer_capacity"}:
            number = parse_int_like(value)
            if number is not None:
                out[key] = number
            continue

        if key == "temperature":
            number = parse_float_like(value)
            if number is not None:
                out[key] = number
            continue

        if key == "connectors":
            if isinstance(value, list):
                connectors = [to_string(x).strip() for x in value if to_string(x).strip()]
                if connectors:
                    out[key] = connectors
            continue

        if key == "extensions" and isinstance(value, dict):
            ext: dict[str, Any] = {}
            event_modules = to_string(value.get("event_modules")).strip()
            if event_modules:
                ext["event_modules"] = event_modules
            event_routes = to_string(value.get("event_routes")).strip()
            if event_routes:
                ext["event_routes"] = event_routes
            if ext:
                out[key] = ext

    return out


def normalize_roles(raw_roles: Any, derived_role_ids: set[str]) -> list[dict[str, Any]]:
    normalized_roles: list[dict[str, Any]] = []
    seen: set[str] = set()

    if isinstance(raw_roles, list):
        for role in raw_roles:
            normalized = normalize_role_entry(role)
            if not normalized:
                continue
            role_id = normalized["id"].strip().lower()
            if role_id in seen:
                continue
            seen.add(role_id)
            normalized_roles.append(normalized)

    for role_id in sorted(derived_role_ids, key=str.lower):
        key = role_id.strip().lower()
        if not key or key in seen:
            continue
        seen.add(key)
        normalized_roles.append(
            {
                "id": role_id.strip(),
                "name": title_from_role_id(role_id),
                "system_prompt": "",
            }
        )

    return normalized_roles


def normalize_step(step: Any, fallback_id: str) -> dict[str, Any] | None:
    if not isinstance(step, dict):
        return None

    step_id = to_string(step.get("id")).strip() or fallback_id
    step_type = to_string(step.get("type")).strip() or "llm_call"

    out: dict[str, Any] = {"id": step_id, "type": step_type}

    target_role = ""
    for key in ("target_role", "role", "agent"):
        value = to_string(step.get(key)).strip()
        if value:
            target_role = value
            break
    if target_role:
        out["target_role"] = target_role

    parameters: dict[str, str] = {}
    raw_parameters = step.get("parameters")
    if isinstance(raw_parameters, dict):
        for key, value in raw_parameters.items():
            normalized_key = to_string(key).strip()
            if not normalized_key:
                continue
            parameters[normalized_key] = to_string(value)

    for key, value in step.items():
        if key in STEP_STRUCTURAL_KEYS:
            continue
        if value is None:
            continue
        normalized_key = to_string(key).strip()
        if not normalized_key:
            continue
        parameters[normalized_key] = to_string(value)

    next_step = to_string(step.get("next")).strip()
    if next_step:
        out["next"] = next_step

    branches = normalize_branches(step.get("branches"))
    if branches:
        out["branches"] = branches

    retry = normalize_retry(step.get("retry"))
    if retry:
        out["retry"] = retry

    on_error = normalize_on_error(step.get("on_error"))
    if on_error:
        out["on_error"] = on_error

    timeout_ms = parse_int_like(step.get("timeout_ms"))
    if timeout_ms is not None:
        out["timeout_ms"] = timeout_ms

    children: list[dict[str, Any]] = []
    for key in ("children", "steps"):
        value = step.get(key)
        if isinstance(value, list):
            for idx, child in enumerate(value, start=1):
                normalized = normalize_step(child, f"{step_id}_{key}_{idx}")
                if normalized:
                    children.append(normalized)

    one_child = step.get("step")
    if isinstance(one_child, dict):
        normalized = normalize_step(one_child, f"{step_id}_step")
        if normalized:
            children.append(normalized)

    if children:
        out["children"] = children

    if parameters:
        out["parameters"] = parameters

    return out


def normalize_configuration(raw_configuration: Any) -> dict[str, Any] | None:
    if not isinstance(raw_configuration, dict):
        return None

    value = raw_configuration.get("closed_world_mode")
    if isinstance(value, bool):
        return {"closed_world_mode": value}
    if isinstance(value, str):
        lowered = value.strip().lower()
        if lowered in {"true", "false"}:
            return {"closed_world_mode": lowered == "true"}
    return None


def normalize_workflow(document: Any, file_stem: str) -> dict[str, Any]:
    if not isinstance(document, dict):
        raise ValueError("top-level YAML must be a mapping")

    name = to_string(document.get("name")).strip() or file_stem
    description = to_string(document.get("description")).strip()

    normalized: dict[str, Any] = {"name": name}
    if description:
        normalized["description"] = description

    configuration = normalize_configuration(document.get("configuration"))
    if configuration:
        normalized["configuration"] = configuration

    raw_steps = document.get("steps")
    if not isinstance(raw_steps, list):
        raw_steps = []

    derived_role_ids: set[str] = set()
    for step in raw_steps:
        collect_role_ids_from_step(step, derived_role_ids)

    normalized["roles"] = normalize_roles(document.get("roles"), derived_role_ids)

    normalized_steps: list[dict[str, Any]] = []
    for idx, step in enumerate(raw_steps, start=1):
        step_obj = normalize_step(step, f"step_{idx}")
        if step_obj:
            normalized_steps.append(step_obj)
    normalized["steps"] = normalized_steps

    return normalized


def dump_yaml(data: dict[str, Any]) -> str:
    text = yaml.safe_dump(
        data,
        sort_keys=False,
        allow_unicode=True,
        width=1000,
    )
    if not text.endswith("\n"):
        text += "\n"
    return text


def migrate_file(path: Path, dry_run: bool, backup_dir: Path | None) -> tuple[str, str]:
    """
    Returns:
      (status, message)
      status in {"migrated", "unchanged", "failed"}
    """
    try:
        original_text = path.read_text(encoding="utf-8")
    except Exception as exc:
        return "failed", f"read failed: {exc}"

    try:
        parsed = yaml.safe_load(original_text)
    except Exception as exc:
        return "failed", f"yaml parse failed: {exc}"

    try:
        normalized = normalize_workflow(parsed, path.stem)
        migrated_text = dump_yaml(normalized)
    except Exception as exc:
        return "failed", f"normalize failed: {exc}"

    if original_text.strip() == migrated_text.strip():
        return "unchanged", "already normalized"

    if dry_run:
        return "migrated", "dry-run preview"

    if backup_dir is None:
        return "failed", "internal error: backup directory missing"

    backup_dir.mkdir(parents=True, exist_ok=True)
    backup_path = backup_dir / path.name
    shutil.copy2(path, backup_path)
    path.write_text(migrated_text, encoding="utf-8")
    return "migrated", f"backup={backup_path}"


def main() -> int:
    args = parse_args()
    workflows_dir = Path(args.workflows_dir).expanduser().resolve()
    if not workflows_dir.exists() or not workflows_dir.is_dir():
        print(f"[error] workflows dir not found: {workflows_dir}", file=sys.stderr)
        return 1

    files = sorted(workflows_dir.glob(args.pattern))
    if not files:
        print(f"[warn] no files matched: {workflows_dir}/{args.pattern}")
        return 0

    backup_dir: Path | None = None
    if not args.dry_run:
        if args.backup_dir:
            backup_dir = Path(args.backup_dir).expanduser().resolve()
        else:
            stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
            backup_dir = workflows_dir / f".legacy-backup-{stamp}"

    migrated = 0
    unchanged = 0
    failed = 0

    print(f"[info] scanning {len(files)} file(s) in {workflows_dir}")
    if args.dry_run:
        print("[info] mode: dry-run")
    else:
        print(f"[info] backup dir: {backup_dir}")

    for file_path in files:
        status, message = migrate_file(file_path, args.dry_run, backup_dir)
        if status == "migrated":
            migrated += 1
            print(f"[migrated] {file_path.name} - {message}")
        elif status == "unchanged":
            unchanged += 1
            print(f"[unchanged] {file_path.name} - {message}")
        else:
            failed += 1
            print(f"[failed] {file_path.name} - {message}", file=sys.stderr)

    print(
        "[summary] migrated={m} unchanged={u} failed={f}".format(
            m=migrated,
            u=unchanged,
            f=failed,
        )
    )
    return 1 if failed > 0 else 0


if __name__ == "__main__":
    raise SystemExit(main())
