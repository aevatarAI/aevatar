#!/usr/bin/env python3
"""Validate Aevatar-native S capability artifacts."""

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


REQUIRED_CAPABILITIES = {
    "budget-monitoring",
    "lark-onboarding-email-approval",
}

ALLOWED_RUNTIMES = {
    "gagent",
    "workflow",
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
        description="Validate Aevatar-native S capability registry, optional workflow YAML, and connector bindings."
    )
    parser.add_argument(
        "--root",
        default=Path(__file__).resolve().parents[2],
        type=Path,
        help="Repository root.",
    )
    parser.add_argument(
        "--registry",
        default="workflows/aevatar-native/s-capabilities.registry.json",
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

    validate_registry_policy(registry, errors)

    capabilities = registry.get("capabilities")
    if not isinstance(capabilities, list):
        errors.append("registry.capabilities must be an array")
        capabilities = []

    found_names: list[str] = []
    for item in capabilities:
        if not isinstance(item, dict):
            errors.append("registry capability entry must be an object")
            continue

        name = str(item.get("name", "")).strip()
        found_names.append(name)
        validate_registry_entry(name, item, registry_dir, connectors, errors)

    missing = sorted(REQUIRED_CAPABILITIES.difference(found_names))
    if missing:
        errors.append(f"required capabilities missing from registry: {', '.join(missing)}")

    scan_paths = [registry_path, connector_catalog_path]
    for item in capabilities:
        if isinstance(item, dict):
            optional_workflow = item.get("optionalWorkflow")
            if isinstance(optional_workflow, dict) and optional_workflow.get("workflowFile"):
                scan_paths.append(registry_dir / str(optional_workflow["workflowFile"]))

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


def validate_registry_policy(registry: dict[str, Any], errors: list[str]) -> None:
    metadata_source = registry.get("metadataSource")
    if not isinstance(metadata_source, dict):
        errors.append("registry.metadataSource is required")
    else:
        if metadata_source.get("owner") != "ornn":
            errors.append("registry.metadataSource.owner must be ornn")
        if metadata_source.get("localArtifactRole") != "bootstrap-mirror":
            errors.append("registry.metadataSource.localArtifactRole must be bootstrap-mirror")

    runtime_policy = registry.get("runtimePolicy")
    if not isinstance(runtime_policy, dict):
        errors.append("registry.runtimePolicy is required")
        return

    if runtime_policy.get("defaultRuntime") != "gagent":
        errors.append("registry.runtimePolicy.defaultRuntime must be gagent")

    allowed = runtime_policy.get("allowedRuntimes")
    if not isinstance(allowed, list) or set(allowed) != ALLOWED_RUNTIMES:
        errors.append("registry.runtimePolicy.allowedRuntimes must contain gagent and workflow")

    disallowed = runtime_policy.get("disallowedRuntimes")
    if not isinstance(disallowed, list) or "n8n" not in disallowed:
        errors.append("registry.runtimePolicy.disallowedRuntimes must include n8n")


def validate_registry_entry(
    name: str,
    item: dict[str, Any],
    registry_dir: Path,
    connectors: dict[str, Any],
    errors: list[str],
) -> None:
    if name not in REQUIRED_CAPABILITIES:
        return

    metadata_ref = str(item.get("metadataRef", "")).strip()
    if not metadata_ref.startswith("ornn://"):
        errors.append(f"{name}: metadataRef must point to Ornn")

    default_runtime = item.get("defaultRuntime")
    if not isinstance(default_runtime, dict):
        errors.append(f"{name}: defaultRuntime is required")
    else:
        if default_runtime.get("kind") != "gagent":
            errors.append(f"{name}: defaultRuntime.kind must be gagent")
        if not str(default_runtime.get("entrypoint", "")).strip():
            errors.append(f"{name}: defaultRuntime.entrypoint is required")

    optional_workflow = item.get("optionalWorkflow")
    if optional_workflow is not None and not isinstance(optional_workflow, dict):
        errors.append(f"{name}: optionalWorkflow must be an object when present")
        optional_workflow = None

    workflow = None
    if isinstance(optional_workflow, dict):
        workflow_file = optional_workflow.get("workflowFile")
        if not isinstance(workflow_file, str) or not workflow_file.strip():
            errors.append(f"{name}: optionalWorkflow.workflowFile is required when optionalWorkflow is present")
        else:
            workflow_path = registry_dir / workflow_file
            workflow = load_workflow(workflow_path, errors)

    step_by_id: dict[str, dict[str, Any]] = {}
    if workflow is not None:
        yaml_name = str(workflow.get("name", "")).strip()
        if yaml_name != name:
            errors.append(f"{name}: workflow YAML name mismatch: {yaml_name}")

        steps = workflow.get("steps")
        if not isinstance(steps, list) or not steps:
            errors.append(f"{name}: optional workflow must define steps")
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
        "aevatarOwnsStateWhenWorkflowRuntimeIsSelected",
        "nyxidOwnsIngressAndCredentials",
        "ornnOwnsCapabilityMetadataAndSkillDiscovery",
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
        if not isinstance(target, dict) or target.get("kind") != "aevatar_capability_run":
            errors.append(f"{name}: nyxidIngress.target.kind must be aevatar_capability_run")
        elif target.get("capability") != name:
            errors.append(f"{name}: nyxidIngress.target.capability must match capability name")

    connector_names = item.get("nyxidConnectors")
    if not isinstance(connector_names, list) or not connector_names:
        errors.append(f"{name}: nyxidConnectors must list at least one connector")
        connector_names = []

    for connector_name in connector_names:
        if connector_name not in connectors:
            errors.append(f"{name}: connector '{connector_name}' is missing from connector catalog")

    if step_by_id:
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
            value = str(contract.get(key, "")).strip()
            if not value:
                errors.append(f"{name}: payloadBuilder.contract.{key} is required")
            elif not value.startswith("ornn://"):
                errors.append(f"{name}: payloadBuilder.contract.{key} must point to Ornn")

    if step_by_id:
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
        if step_by_id and (not step_id or step_id not in step_by_id):
            errors.append(f"{name}: Ornn binding references missing step '{step_id}'")
            continue
        if not skill:
            errors.append(f"{name}: Ornn binding for step '{step_id}' must declare skill")
        if purpose == "payload_construction":
            has_payload_binding = True

        tool = str(binding.get("tool", "")).strip()
        if step_by_id:
            step = step_by_id[step_id]
            parameters = step.get("parameters")
            parameters = parameters if isinstance(parameters, dict) else {}
            step_type = str(step.get("type", "")).strip()
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

    names = ", ".join(sorted(name for name in found_names if name in REQUIRED_CAPABILITIES))
    print(f"Aevatar-native S capability validation passed: {names}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
