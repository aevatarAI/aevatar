#!/usr/bin/env python3
"""Validate Aevatar-native S workflow artifacts."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any

try:
    import yaml
except ImportError as exc:  # pragma: no cover - depends on local environment
    raise SystemExit("PyYAML is required: python3 -m pip install pyyaml") from exc


REQUIRED_WORKFLOWS = {
    "budget-monitoring",
    "lark-onboarding-email-approval",
}

DISALLOWED_RUNTIME_MARKERS = (
    "n8n-nodes-base",
    "n8n.",
    "n8n:",
    "n8n runtime",
    "$(",
)

SECRET_KEY_PATTERN = re.compile(
    r"(secret|token|api[_-]?key|authorization|password|credential)",
    re.IGNORECASE,
)
SECRET_VALUE_PATTERN = re.compile(
    r"(Bearer\s+[A-Za-z0-9._~+/=-]{16,}|"
    r"sk-[A-Za-z0-9]{16,}|"
    r"cli_[A-Za-z0-9]{12,}|"
    r"[A-Za-z0-9_=-]{32,})"
)
PLACEHOLDER_PATTERN = re.compile(r"^\$\{[A-Z0-9_]+}$")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Validate Aevatar-native S workflow registry, workflow YAML, and connector bindings."
    )
    parser.add_argument(
        "--root",
        default=Path(__file__).resolve().parents[2],
        type=Path,
        help="Repository root.",
    )
    parser.add_argument(
        "--registry",
        default="workflows/aevatar-native/s-workflows.registry.json",
        help="Registry path relative to the repository root.",
    )
    args = parser.parse_args()

    root = args.root.resolve()
    registry_path = (root / args.registry).resolve()
    errors: list[str] = []

    if not registry_path.exists():
        errors.append(f"registry not found: {registry_path}")
        return report(errors, [])

    registry = load_json(registry_path, errors)
    if not isinstance(registry, dict):
        errors.append("registry root must be a JSON object")
        return report(errors, [])

    registry_dir = registry_path.parent
    connector_catalog_path = registry_dir / str(registry.get("connectorCatalog", ""))
    connectors = load_connectors(connector_catalog_path, errors)

    workflows = registry.get("workflows")
    if not isinstance(workflows, list):
        errors.append("registry.workflows must be an array")
        workflows = []

    found_names: list[str] = []
    for item in workflows:
        if not isinstance(item, dict):
            errors.append("registry workflow entry must be an object")
            continue

        name = str(item.get("name", "")).strip()
        found_names.append(name)
        validate_registry_entry(name, item, registry_dir, connectors, errors)

    missing = sorted(REQUIRED_WORKFLOWS.difference(found_names))
    if missing:
        errors.append(f"required workflows missing from registry: {', '.join(missing)}")

    scan_paths = [registry_path, connector_catalog_path]
    for item in workflows:
        if isinstance(item, dict) and item.get("workflowFile"):
            scan_paths.append(registry_dir / str(item["workflowFile"]))

    validate_no_real_secrets(scan_paths, errors)
    validate_no_external_runtime_markers(scan_paths, errors)

    return report(errors, found_names)


def load_json(path: Path, errors: list[str]) -> Any:
    if not path.exists():
        errors.append(f"json file not found: {path}")
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        errors.append(f"invalid json {path}: {exc}")
        return None


def load_connectors(path: Path, errors: list[str]) -> dict[str, Any]:
    data = load_json(path, errors)
    if not isinstance(data, dict):
        return {}

    node = data.get("connectors", data)
    connectors: dict[str, Any] = {}
    if isinstance(node, dict):
        definitions = node.get("definitions")
        if isinstance(definitions, list):
            for entry in definitions:
                if isinstance(entry, dict) and entry.get("name"):
                    connectors[str(entry["name"])] = entry
        else:
            for key, value in node.items():
                if isinstance(value, dict):
                    connectors[str(value.get("name") or key)] = value
    elif isinstance(node, list):
        for entry in node:
            if isinstance(entry, dict) and entry.get("name"):
                connectors[str(entry["name"])] = entry

    if not connectors:
        errors.append(f"connector catalog has no connectors: {path}")
    return connectors


def validate_registry_entry(
    name: str,
    item: dict[str, Any],
    registry_dir: Path,
    connectors: dict[str, Any],
    errors: list[str],
) -> None:
    if name not in REQUIRED_WORKFLOWS:
        return

    workflow_file = item.get("workflowFile")
    if not isinstance(workflow_file, str) or not workflow_file.strip():
        errors.append(f"{name}: workflowFile is required")
        return

    workflow_path = registry_dir / workflow_file
    workflow = load_workflow(workflow_path, errors)
    if workflow is None:
        return

    yaml_name = str(workflow.get("name", "")).strip()
    if yaml_name != name:
        errors.append(f"{name}: workflow YAML name mismatch: {yaml_name}")

    steps = workflow.get("steps")
    if not isinstance(steps, list) or not steps:
        errors.append(f"{name}: workflow must define steps")
        steps = []

    step_by_id = {
        str(step.get("id")): step
        for step in steps
        if isinstance(step, dict) and step.get("id")
    }

    validate_aevatar_runtime_assertions(name, item, errors)
    validate_payload_builder_binding(name, item, step_by_id, errors)
    validate_nyxid_binding(name, item, step_by_id, connectors, errors)
    validate_ornn_bindings(name, item, step_by_id, errors)


def load_workflow(path: Path, errors: list[str]) -> dict[str, Any] | None:
    if not path.exists():
        errors.append(f"workflow file not found: {path}")
        return None
    try:
        loaded = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as exc:
        errors.append(f"invalid workflow yaml {path}: {exc}")
        return None
    if not isinstance(loaded, dict):
        errors.append(f"workflow yaml root must be an object: {path}")
        return None
    return loaded


def validate_aevatar_runtime_assertions(name: str, item: dict[str, Any], errors: list[str]) -> None:
    assertions = item.get("runtimeAssertions")
    if not isinstance(assertions, dict):
        errors.append(f"{name}: runtimeAssertions is required")
        return

    required_true = (
        "aevatarOwnsState",
        "nyxidOwnsIngressAndCredentials",
        "ornnOwnsSkillDiscovery",
    )
    for key in required_true:
        if assertions.get(key) is not True:
            errors.append(f"{name}: runtimeAssertions.{key} must be true")

    if assertions.get("dependsOnExternalRuntime") is not False:
        errors.append(f"{name}: runtimeAssertions.dependsOnExternalRuntime must be false")


def validate_nyxid_binding(
    name: str,
    item: dict[str, Any],
    step_by_id: dict[str, dict[str, Any]],
    connectors: dict[str, Any],
    errors: list[str],
) -> None:
    ingress = item.get("nyxidIngress")
    if not isinstance(ingress, dict):
        errors.append(f"{name}: nyxidIngress is required")
    else:
        if not ingress.get("route"):
            errors.append(f"{name}: nyxidIngress.route is required")
        target = ingress.get("target")
        if not isinstance(target, dict) or target.get("kind") != "aevatar_workflow_run":
            errors.append(f"{name}: nyxidIngress.target.kind must be aevatar_workflow_run")
        elif target.get("workflow") != name:
            errors.append(f"{name}: nyxidIngress.target.workflow must match workflow name")

    connector_names = item.get("nyxidConnectors")
    if not isinstance(connector_names, list) or not connector_names:
        errors.append(f"{name}: nyxidConnectors must list at least one connector")
        connector_names = []

    for connector_name in connector_names:
        if connector_name not in connectors:
            errors.append(f"{name}: connector '{connector_name}' is missing from connector catalog")

    workflow_connector_refs = set()
    for step in step_by_id.values():
        parameters = step.get("parameters")
        if isinstance(parameters, dict) and parameters.get("connector"):
            workflow_connector_refs.add(str(parameters["connector"]))

    for connector_name in connector_names:
        if connector_name not in workflow_connector_refs:
            errors.append(f"{name}: connector '{connector_name}' is not referenced by workflow steps")


def validate_payload_builder_binding(
    name: str,
    item: dict[str, Any],
    step_by_id: dict[str, dict[str, Any]],
    errors: list[str],
) -> None:
    payload_builder = item.get("payloadBuilder")
    if not isinstance(payload_builder, dict):
        errors.append(f"{name}: payloadBuilder is required")
        return

    if payload_builder.get("kind") != "ornn_skill_binding":
        errors.append(f"{name}: payloadBuilder.kind must be ornn_skill_binding")
    if payload_builder.get("tool") != "use_skill":
        errors.append(f"{name}: payloadBuilder.tool must be use_skill")

    skill = str(payload_builder.get("skill", "")).strip()
    if not skill:
        errors.append(f"{name}: payloadBuilder.skill is required")

    contract = payload_builder.get("contract")
    if not isinstance(contract, dict):
        errors.append(f"{name}: payloadBuilder.contract is required")
    else:
        for key in ("inputRef", "outputRef"):
            if not str(contract.get(key, "")).strip():
                errors.append(f"{name}: payloadBuilder.contract.{key} is required")

    matching_steps = []
    for step in step_by_id.values():
        parameters = step.get("parameters")
        parameters = parameters if isinstance(parameters, dict) else {}
        if (
            step.get("type") == "tool_call"
            and parameters.get("tool") == "use_skill"
            and parameters.get("skill_binding") == skill
            and parameters.get("required_for") == "payload_construction"
        ):
            matching_steps.append(step)

    if not matching_steps:
        errors.append(
            f"{name}: payloadBuilder.skill must be used by a tool_call/use_skill payload construction step"
        )


def validate_ornn_bindings(
    name: str,
    item: dict[str, Any],
    step_by_id: dict[str, dict[str, Any]],
    errors: list[str],
) -> None:
    bindings = item.get("ornnSkillBindings")
    if not isinstance(bindings, list) or not bindings:
        errors.append(f"{name}: ornnSkillBindings must list at least one binding")
        return

    has_payload_binding = False
    for binding in bindings:
        if not isinstance(binding, dict):
            errors.append(f"{name}: Ornn binding must be an object")
            continue

        step_id = str(binding.get("stepId", "")).strip()
        skill = str(binding.get("skill", "")).strip()
        purpose = str(binding.get("purpose", "")).strip()
        if not step_id or step_id not in step_by_id:
            errors.append(f"{name}: Ornn binding references missing step '{step_id}'")
            continue
        if not skill:
            errors.append(f"{name}: Ornn binding for step '{step_id}' must declare skill")
        if purpose == "payload_construction":
            has_payload_binding = True

        step = step_by_id[step_id]
        parameters = step.get("parameters")
        parameters = parameters if isinstance(parameters, dict) else {}
        step_type = str(step.get("type", "")).strip()
        tool = str(binding.get("tool", "")).strip()
        if tool == "use_skill" and not (
            step_type == "tool_call" and parameters.get("tool") == "use_skill"
        ):
            errors.append(f"{name}: use_skill binding step '{step_id}' must be a tool_call/use_skill step")

    if not has_payload_binding:
        errors.append(f"{name}: at least one Ornn binding must cover payload_construction")


def validate_no_real_secrets(paths: list[Path], errors: list[str]) -> None:
    for path in paths:
        if not path.exists():
            continue
        text = path.read_text(encoding="utf-8")
        scan_node_for_secret_values(path, parse_structured_text(path, text), errors)
        for line_no, line in enumerate(text.splitlines(), start=1):
            if looks_like_standalone_secret(line) and not is_placeholder_line(line):
                errors.append(f"{path}:{line_no}: possible hard-coded secret value")


def parse_structured_text(path: Path, text: str) -> Any:
    try:
        if path.suffix.lower() == ".json":
            return json.loads(text)
        if path.suffix.lower() in {".yaml", ".yml"}:
            return yaml.safe_load(text)
    except Exception:
        return None
    return None


def scan_node_for_secret_values(path: Path, node: Any, errors: list[str], trail: str = "$") -> None:
    if isinstance(node, dict):
        for key, value in node.items():
            child_trail = f"{trail}.{key}"
            if SECRET_KEY_PATTERN.search(str(key)) and isinstance(value, str):
                if value and not is_placeholder_value(value):
                    errors.append(f"{path}:{child_trail}: secret-like key must use an environment placeholder")
            scan_node_for_secret_values(path, value, errors, child_trail)
    elif isinstance(node, list):
        for index, value in enumerate(node):
            scan_node_for_secret_values(path, value, errors, f"{trail}[{index}]")


def is_placeholder_line(line: str) -> bool:
    return "${" in line and "}" in line


def looks_like_standalone_secret(line: str) -> bool:
    stripped = line.strip()
    if not stripped:
        return False

    lower = stripped.lower()
    if any(prefix in lower for prefix in ("bearer ", "authorization:", "api_key:", "api-key:", "token:", "secret:")):
        return bool(SECRET_VALUE_PATTERN.search(stripped))

    if re.fullmatch(r"[A-Za-z0-9_./+=-]{40,}", stripped):
        return True

    if re.search(r"\b(sk-[A-Za-z0-9]{16,}|xox[baprs]-[A-Za-z0-9-]{16,})\b", stripped):
        return True

    return False


def is_placeholder_value(value: str) -> bool:
    stripped = value.strip()
    if PLACEHOLDER_PATTERN.match(stripped):
        return True
    return "${" in stripped and "}" in stripped and not SECRET_VALUE_PATTERN.search(
        stripped.replace("${", "").replace("}", "")
    )


def validate_no_external_runtime_markers(paths: list[Path], errors: list[str]) -> None:
    for path in paths:
        if not path.exists():
            continue
        lowered = path.read_text(encoding="utf-8").lower()
        for marker in DISALLOWED_RUNTIME_MARKERS:
            if marker in lowered:
                errors.append(f"{path}: disallowed external runtime marker '{marker}'")


def report(errors: list[str], found_names: list[str]) -> int:
    if errors:
        print("Aevatar-native S workflow validation failed:")
        for error in errors:
            print(f"- {error}")
        return 1

    names = ", ".join(sorted(name for name in found_names if name in REQUIRED_WORKFLOWS))
    print(f"Aevatar-native S workflow validation passed: {names}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
