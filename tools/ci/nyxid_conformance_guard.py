#!/usr/bin/env python3

import argparse
import hashlib
import json
import subprocess
import tempfile
from pathlib import Path

from nyxid_semantic_evaluation import validate_semantic_evaluation


REPO_ROOT = Path(__file__).resolve().parents[2]
CONTRACT_ROOT = REPO_ROOT / "docs/contracts/nyxid-assistant-conformance/v1"
MAX_DIAGNOSTICS = 20


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path: Path):
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--nyxid-root", type=Path)
    parser.add_argument("--gist-root", type=Path)
    args = parser.parse_args()
    errors = []

    sources = load_json(CONTRACT_ROOT / "sources.json")
    leaves = load_json(CONTRACT_ROOT / "cli-leaves.json")
    coverage = load_json(CONTRACT_ROOT / "coverage-manifest.json")
    registry = load_json(CONTRACT_ROOT / "registry-v4.json")
    tolerant = load_json(CONTRACT_ROOT / "tolerant-reader-fixtures.json")
    adversarial = load_json(CONTRACT_ROOT / "adversarial-fixtures.json")
    semantic = load_json(CONTRACT_ROOT / "semantic-evaluation.json")

    validate_local_digests(sources, errors)
    validate_leaf_coverage(leaves, coverage, registry, errors)
    validate_fixture_layers(tolerant, adversarial, errors)
    validate_semantic_evaluation(semantic, coverage, CONTRACT_ROOT, REPO_ROOT, errors)

    if args.nyxid_root:
        validate_nyxid(args.nyxid_root.resolve(), sources, leaves, errors)
    if args.gist_root:
        validate_gist(args.gist_root.resolve(), sources, errors)

    if errors:
        print("NyxID conformance guard failed:")
        for error in errors[:MAX_DIAGNOSTICS]:
            print(f"  - {error}")
        if len(errors) > MAX_DIAGNOSTICS:
            print(f"  - ... {len(errors) - MAX_DIAGNOSTICS} more diagnostic(s) omitted")
        return 1

    print(
        "NyxID conformance guard passed: "
        f"{leaves['top_level_count']} top-level commands, "
        f"{leaves['leaf_count']} leaves, one outcome row per leaf."
    )
    return 0


def validate_local_digests(sources, errors):
    aevatar = sources["aevatar"]
    digest_lines = []
    for relative_path, expected in sorted(aevatar["files"].items()):
        path = REPO_ROOT / relative_path
        if not path.is_file():
            errors.append(f"pinned Aevatar source is missing: {relative_path}")
            continue
        actual = sha256(path)
        if actual != expected:
            errors.append(
                f"Aevatar source drift: {relative_path} expected {expected[:12]}, got {actual[:12]}"
            )
        digest_lines.append(f"{actual}  {relative_path}\n")
    aggregate = hashlib.sha256("".join(digest_lines).encode()).hexdigest()
    if aggregate != aevatar["contract_files_sha256"]:
        errors.append(
            "Aevatar contract aggregate drift: "
            f"expected {aevatar['contract_files_sha256'][:12]}, got {aggregate[:12]}"
        )

    for relative_path, expected in sources["generated_artifacts"].items():
        path = CONTRACT_ROOT / relative_path
        if not path.is_file():
            errors.append(f"generated conformance artifact is missing: {relative_path}")
        elif sha256(path) != expected:
            errors.append(f"generated conformance artifact drift: {relative_path}")

    registry_pin = sources["assistant_registry"]
    payload = CONTRACT_ROOT / registry_pin["checked_in_payload"]
    if sha256(payload) != registry_pin["checked_in_payload_sha256"]:
        errors.append("checked-in assistant registry payload digest drift")


def validate_leaf_coverage(leaves, coverage, registry, errors):
    leaf_rows = leaves.get("leaves", [])
    leaf_paths = [row.get("path") for row in leaf_rows]
    if leaves.get("leaf_count") != len(leaf_rows):
        errors.append("cli-leaves leaf_count is not derived from the leaf array")
    if leaves.get("top_level_count") != len({path.split(" ", 1)[0] for path in leaf_paths}):
        errors.append("cli-leaves top_level_count is not derived from leaf paths")
    if leaf_paths != sorted(leaf_paths):
        errors.append("cli-leaves must be sorted by full command path")
    duplicates = sorted({path for path in leaf_paths if leaf_paths.count(path) > 1})
    if duplicates:
        errors.extend(f"duplicate CLI leaf: {path}" for path in duplicates)

    rows = coverage.get("rows", [])
    coverage_paths = [row.get("cli_path") for row in rows]
    if coverage.get("leaf_count") != len(rows):
        errors.append("coverage leaf_count is not derived from coverage rows")
    for path in sorted(set(leaf_paths) - set(coverage_paths)):
        errors.append(f"CLI leaf has no outcome row: {path}")
    for path in sorted(set(coverage_paths) - set(leaf_paths)):
        errors.append(f"outcome row has no CLI leaf: {path}")
    duplicates = sorted({path for path in coverage_paths if coverage_paths.count(path) > 1})
    errors.extend(f"CLI leaf has multiple outcome rows: {path}" for path in duplicates)

    allowed_classes = {"R", "A", "P", "L", "X", "CONTROL"}
    required = {
        "operation_id",
        "operation_class",
        "outcome_class",
        "mechanism",
        "availability",
        "availability_predicate",
        "approval_authority",
        "evidence_type",
        "owning_issue",
    }
    registry_actions = {entry["action"] for entry in registry.get("actions", [])}
    for row in rows:
        path = row.get("cli_path", "<missing>")
        missing = sorted(field for field in required if not row.get(field))
        if missing:
            errors.append(f"{path}: missing required fields {', '.join(missing)}")
        if row.get("operation_class") not in allowed_classes:
            errors.append(f"{path}: unknown operation class {row.get('operation_class')}")
        if row.get("operation_class") in {"A", "L"} and not row.get("utterances"):
            errors.append(f"{path}: Class-{row.get('operation_class')} row has no utterance fixture")
        if row.get("availability") == "recognized_but_unavailable" and row.get(
            "outcome_class"
        ) not in {"honest_decline", "honest_cannot_check"}:
            errors.append(f"{path}: unavailable row does not fail closed honestly")
        if row.get("operation_class") == "L":
            expected_command = f"nyxid {path}"
            if row.get("handoff_command") != expected_command:
                errors.append(f"{path}: handoff command must be exactly '{expected_command}'")
            if row.get("evidence_type") != "no_aevatar_execution_claim":
                errors.append(f"{path}: local handoff cannot claim Aevatar execution")
        if row.get("operation_class") == "X" and row.get("evidence_type") != "no_effect_receipt":
            errors.append(f"{path}: excluded row cannot carry an effect receipt")
        if row.get("operation_class") == "A" and row.get("status") == "shipped":
            artifacts = row.get("artifacts", [])
            if len(artifacts) != 4 or len(set(artifacts)) != 4:
                errors.append(f"{path}: shipped Class-A row must name exactly four artifacts")
            for artifact in artifacts:
                if not (REPO_ROOT / artifact).is_file():
                    errors.append(f"{path}: Class-A artifact is missing: {artifact}")
            if row.get("operation_id") not in registry_actions:
                errors.append(f"{path}: shipped Class-A operation is absent from pinned registry")
            if row.get("evidence_type") != "typed_postcondition_read_model":
                errors.append(f"{path}: shipped Class-A row lacks typed postcondition evidence")
        if row.get("operation_class") == "R" and row.get("status") == "shipped":
            if not row.get("mounted_tool") or row.get("availability") != "mounted":
                errors.append(f"{path}: shipped Class-R row does not name a mounted tool")

    derived_counts = {
        operation_class: sum(row.get("operation_class") == operation_class for row in rows)
        for operation_class in ["R", "A", "P", "L", "X", "CONTROL"]
    }
    if coverage.get("class_counts") != derived_counts:
        errors.append("coverage class_counts are not derived from coverage rows")


def validate_fixture_layers(tolerant, adversarial, errors):
    tolerant_ids = {fixture.get("id") for fixture in tolerant.get("fixtures", [])}
    required_tolerant = {
        "unknown-root-member",
        "unknown-member-on-known-descriptor",
        "unknown-verb",
        "unknown-risk-enum",
        "unknown-tier-enum",
        "open-object-schema",
        "forbidden-secret-schema-member",
        "destructive-remember-policy",
        "malformed-json",
    }
    if not required_tolerant.issubset(tolerant_ids):
        errors.append("tolerant-reader fixture corpus is incomplete")

    fixtures = adversarial.get("fixtures", [])
    categories = {fixture.get("category") for fixture in fixtures}
    required_categories = {
        "indirect_prompt_injection",
        "identity_confusion",
        "command_ordering",
        "leakage",
        "tolerant_reader",
        "honesty",
    }
    if not required_categories.issubset(categories):
        errors.append("adversarial fixture categories are incomplete")
    if any(fixture.get("effect_receipt_allowed") is not False for fixture in fixtures):
        errors.append("adversarial fixtures must fail closed before an effect receipt")
    identity = next(
        (fixture for fixture in fixtures if fixture.get("category") == "identity_confusion"),
        {},
    )
    identities = {
        identity.get("member_id"),
        identity.get("workflow_id"),
        identity.get("published_service_id"),
    }
    if identities != {"m-alpha", "wf-alpha", "svc-alpha"}:
        errors.append("identity-confusion fixture must keep member/workflow/service IDs distinct")

def validate_nyxid(root, sources, expected_leaves, errors):
    pin = sources["nyxid"]
    revision = git_output(
        root, "rev-parse", "HEAD", errors=errors, label="NyxID revision"
    )
    if revision and revision != pin["revision"]:
        errors.append(f"NyxID revision drift: expected {pin['revision'][:12]}, got {revision[:12]}")
    checks = {
        pin["cli_source"]: pin["cli_source_sha256"],
        "Cargo.lock": pin["cargo_lock_sha256"],
        sources["assistant_registry"]["nyxid_source"]: sources["assistant_registry"][
            "nyxid_source_sha256"
        ],
    }
    for relative_path, expected in checks.items():
        path = root / relative_path
        if not path.is_file():
            errors.append(f"pinned NyxID source is missing: {relative_path}")
        elif sha256(path) != expected:
            errors.append(f"NyxID source drift: {relative_path}")

    with tempfile.TemporaryDirectory() as directory:
        generated = Path(directory) / "cli-leaves.json"
        result = subprocess.run(
            [
                str(REPO_ROOT / "tools/nyxid-conformance/export-cli-leaves.sh"),
                str(root),
                str(generated),
            ],
            cwd=REPO_ROOT,
            text=True,
            capture_output=True,
            check=False,
        )
        if result.returncode != 0:
            errors.append(f"Cli::command() export failed: {result.stderr.strip()[-300:]}")
        elif load_json(generated) != expected_leaves:
            actual = load_json(generated)
            expected_paths = {leaf["path"] for leaf in expected_leaves["leaves"]}
            actual_paths = {leaf["path"] for leaf in actual["leaves"]}
            for path in sorted(actual_paths - expected_paths):
                errors.append(f"NyxID clap leaf missing from checked-in artifact: {path}")
            for path in sorted(expected_paths - actual_paths):
                errors.append(f"checked-in leaf absent from NyxID clap tree: {path}")
            if expected_paths == actual_paths:
                errors.append("NyxID clap leaf metadata drifted")


def validate_gist(root, sources, errors):
    pin = sources["support_contract"]
    revision = git_output(
        root,
        "rev-parse",
        "HEAD",
        errors=errors,
        label="support-contract revision",
    )
    if revision and revision != pin["revision"]:
        errors.append(
            f"support-contract revision drift: expected {pin['revision'][:12]}, got {revision[:12]}"
        )
    source = root / pin["english_source"]
    if not source.is_file() or sha256(source) != pin["english_source_sha256"]:
        errors.append("support-contract English source digest drift")


def git_output(root, *arguments, errors, label):
    result = subprocess.run(
        ["git", *arguments], cwd=root, text=True, capture_output=True, check=False
    )
    if result.returncode != 0:
        errors.append(f"cannot read {label}: {result.stderr.strip()[-200:]}")
        return None
    return result.stdout.strip()


if __name__ == "__main__":
    raise SystemExit(main())
