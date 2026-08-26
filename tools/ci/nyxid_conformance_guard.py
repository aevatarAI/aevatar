#!/usr/bin/env python3

import argparse
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path

from nyxid_semantic_evaluation import validate_semantic_evaluation


REPO_ROOT = Path(__file__).resolve().parents[2]
CONTRACT_ROOT = REPO_ROOT / "docs/contracts/nyxid-assistant-conformance/v1"
CODE_EXECUTION_CONTRACT_ROOT = (
    REPO_ROOT / "docs/contracts/nyxid-code-execution-conformance/v1"
)
MAX_DIAGNOSTICS = 20


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path: Path):
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--nyxid-root", type=Path)
    parser.add_argument(
        "--nyxid-wire-root",
        type=Path,
        help="validate only the code-execution wire surfaces against a NyxID checkout",
    )
    parser.add_argument("--gist-root", type=Path)
    parser.add_argument(
        "--refresh-aevatar-revision",
        metavar="REVISION",
        help="rewrite the Aevatar source pin from the named commit and exit",
    )
    args = parser.parse_args()
    errors = []

    if args.nyxid_wire_root and (
        args.nyxid_root or args.gist_root or args.refresh_aevatar_revision
    ):
        parser.error("--nyxid-wire-root is an independent validation mode")

    if args.nyxid_wire_root:
        wire_sources = load_json(CODE_EXECUTION_CONTRACT_ROOT / "sources.json")
        observed_revision = validate_nyxid_wire_source(
            args.nyxid_wire_root.resolve(),
            wire_sources,
            errors,
        )
        if errors:
            print("NyxID code-execution wire guard failed:")
            for error in errors[:MAX_DIAGNOSTICS]:
                print(f"  - {error}")
            if len(errors) > MAX_DIAGNOSTICS:
                print(f"  - ... {len(errors) - MAX_DIAGNOSTICS} more diagnostic(s) omitted")
            return 1
        print(
            "NyxID code-execution wire guard passed at "
            f"{observed_revision[:12]}."
        )
        return 0

    sources = load_json(CONTRACT_ROOT / "sources.json")
    if args.refresh_aevatar_revision:
        refresh_aevatar_pin(
            REPO_ROOT,
            sources,
            args.refresh_aevatar_revision,
            errors,
        )
        if errors:
            print("NyxID conformance pin refresh failed:")
            for error in errors[:MAX_DIAGNOSTICS]:
                print(f"  - {error}")
            return 1
        (CONTRACT_ROOT / "sources.json").write_text(
            json.dumps(sources, indent=2) + "\n",
            encoding="utf-8",
        )
        print(
            "Refreshed Aevatar conformance pin from revision "
            f"{sources['aevatar']['revision']}."
        )
        return 0

    leaves = load_json(CONTRACT_ROOT / "cli-leaves.json")
    coverage = load_json(CONTRACT_ROOT / "coverage-manifest.json")
    registry = load_json(CONTRACT_ROOT / sources["assistant_registry"]["checked_in_payload"])
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
    validate_aevatar_digests(REPO_ROOT, aevatar, errors)

    for relative_path, expected in sources["generated_artifacts"].items():
        path = CONTRACT_ROOT / relative_path
        if not path.is_file():
            errors.append(f"generated conformance artifact is missing: {relative_path}")
        elif sha256(path) != expected:
            errors.append(f"generated conformance artifact drift: {relative_path}")

    validate_assistant_registry_digests(
        CONTRACT_ROOT,
        sources["assistant_registry"],
        errors,
    )


def validate_assistant_registry_digests(contract_root, registry_pin, errors):
    payload = contract_root / registry_pin["checked_in_payload"]
    if not payload.is_file():
        errors.append("checked-in assistant registry payload is missing")
    elif sha256(payload) != registry_pin["checked_in_payload_sha256"]:
        errors.append("checked-in assistant registry payload digest drift")

    transition_payloads = registry_pin.get("transition_payloads", {})
    accepted_revisions = registry_pin.get("accepted_revisions", [])
    if set(accepted_revisions) != set(transition_payloads):
        errors.append("assistant registry transition revisions do not match their payload pins")
    current_revision = registry_pin.get("revision")
    if current_revision not in accepted_revisions:
        errors.append("current assistant registry revision is not transition-accepted")
    current_transition = transition_payloads.get(current_revision)
    if current_transition is not None and (
        current_transition.get("checked_in_payload") != registry_pin.get("checked_in_payload")
        or current_transition.get("checked_in_payload_sha256")
        != registry_pin.get("checked_in_payload_sha256")
    ):
        errors.append("current assistant registry payload does not match its transition pin")

    for revision, transition_pin in transition_payloads.items():
        transition_payload = contract_root / transition_pin["checked_in_payload"]
        if not transition_payload.is_file():
            errors.append(f"assistant registry transition payload is missing: {revision}")
            continue
        if sha256(transition_payload) != transition_pin["checked_in_payload_sha256"]:
            errors.append(f"assistant registry transition payload digest drift: {revision}")
            continue
        if load_json(transition_payload).get("revision") != revision:
            errors.append(f"assistant registry transition payload revision mismatch: {revision}")


def validate_aevatar_digests(repo_root, aevatar, errors):
    revision = aevatar.get("revision", "")
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        errors.append("pinned Aevatar revision must be a full lowercase commit SHA")
        return
    if not git_commit_exists(repo_root, revision, errors, "Aevatar revision"):
        return
    head_revision = resolve_git_commit(repo_root, "HEAD", errors, "Aevatar HEAD")
    if head_revision is None:
        return
    if not git_is_ancestor(repo_root, revision, head_revision):
        errors.append("pinned Aevatar revision must be an ancestor of current HEAD")

    digest_lines = []
    for relative_path, expected in sorted(aevatar["files"].items()):
        content = git_blob(
            repo_root,
            revision,
            relative_path,
            errors,
            "pinned Aevatar source",
        )
        if content is None:
            continue
        head_content = git_blob(
            repo_root,
            head_revision,
            relative_path,
            errors,
            "current Aevatar HEAD source",
        )
        if head_content is not None and head_content != content:
            errors.append(
                "Aevatar HEAD source differs from declared revision: "
                f"{relative_path}"
            )
        actual = hashlib.sha256(content).hexdigest()
        if actual != expected:
            errors.append(
                "Aevatar declared-tree source drift: "
                f"{relative_path} expected {expected[:12]}, got {actual[:12]}"
            )
        digest_lines.append(f"{actual}  {relative_path}\n")
    aggregate = hashlib.sha256("".join(digest_lines).encode()).hexdigest()
    if aggregate != aevatar["contract_files_sha256"]:
        errors.append(
            "Aevatar contract aggregate drift: "
            f"expected {aevatar['contract_files_sha256'][:12]}, got {aggregate[:12]}"
        )


def refresh_aevatar_pin(repo_root, sources, requested_revision, errors):
    revision = resolve_git_commit(repo_root, requested_revision, errors, "Aevatar revision")
    if revision is None:
        return

    aevatar = sources["aevatar"]
    refreshed_files = {}
    digest_lines = []
    for relative_path in sorted(aevatar["files"]):
        content = git_blob(
            repo_root,
            revision,
            relative_path,
            errors,
            "pinned Aevatar source",
        )
        if content is None:
            continue
        digest = hashlib.sha256(content).hexdigest()
        refreshed_files[relative_path] = digest
        digest_lines.append(f"{digest}  {relative_path}\n")

    if errors:
        return
    aevatar["revision"] = revision
    aevatar["files"] = refreshed_files
    aevatar["contract_files_sha256"] = hashlib.sha256(
        "".join(digest_lines).encode()
    ).hexdigest()


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

    # Shape alone proves nothing about the system: every fixture must also be bound to an
    # executor in the harness that runs it against production code. The harness fails on an
    # unbound fixture by itself; this keeps the corpus from going decorative again if the
    # harness is removed wholesale.
    harness = REPO_ROOT / "test/Aevatar.AI.Tests/NyxIdAdversarialCorpusTests.cs"
    if not harness.is_file():
        errors.append("adversarial corpus has no executing harness")
    else:
        harness_source = harness.read_text(encoding="utf-8")
        for fixture in fixtures:
            fixture_id = fixture.get("id")
            if f'"{fixture_id}"' not in harness_source:
                errors.append(f"adversarial fixture is not executed by the harness: {fixture_id}")

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


def validate_nyxid_wire_source(root, sources, errors):
    validate_nyxid_wire_manifest(sources, errors)

    reviewed_revision = sources.get("nyxid", {}).get("reviewed_revision", "")
    observed_revision = resolve_git_commit(root, "HEAD", errors, "NyxID wire HEAD")
    if (
        re.fullmatch(r"[0-9a-f]{40}", reviewed_revision)
        and git_commit_exists(root, reviewed_revision, errors, "NyxID reviewed revision")
        and observed_revision is not None
        and not git_is_ancestor(root, reviewed_revision, observed_revision)
    ):
        errors.append(
            "NyxID wire HEAD must be the reviewed revision or one of its descendants"
        )

    validate_nyxid_wire_contract(root, sources.get("wire_contract", {}), errors)
    return observed_revision or "unknown"


def validate_nyxid_wire_manifest(sources, errors):
    if sources.get("schema_version") != 1:
        errors.append("NyxID wire manifest schema_version must be 1")

    nyxid = sources.get("nyxid", {})
    if nyxid.get("repository") != "https://github.com/ChronoAIProject/NyxID.git":
        errors.append("NyxID wire manifest repository is not the canonical source")
    if nyxid.get("tracked_ref") != "main":
        errors.append("NyxID wire manifest must track main")
    if not re.fullmatch(r"[0-9a-f]{40}", nyxid.get("reviewed_revision", "")):
        errors.append("NyxID wire reviewed_revision must be a full lowercase commit SHA")

    wire_contract = sources.get("wire_contract", {})
    if wire_contract.get("revision") != "nyxid-code-execution-wire.v1":
        errors.append("NyxID wire contract revision is unsupported")

    files = wire_contract.get("files", {})
    if not files:
        errors.append("NyxID wire manifest must pin at least one source file")
    for relative_path, digest in files.items():
        path = Path(relative_path)
        if path.is_absolute() or ".." in path.parts:
            errors.append(f"NyxID wire source path escapes the checkout: {relative_path}")
        if not re.fullmatch(r"[0-9a-f]{64}", digest):
            errors.append(f"NyxID wire source digest is invalid: {relative_path}")


def validate_nyxid_wire_contract(root, wire_contract, errors):

    files = wire_contract.get("files", {})
    required_markers = wire_contract.get("required_markers", {})
    forbidden_markers = wire_contract.get("forbidden_markers", {})
    declared_paths = set(files)
    marker_paths = set(required_markers) | set(forbidden_markers)
    if marker_paths - declared_paths:
        for relative_path in sorted(marker_paths - declared_paths):
            errors.append(
                "NyxID wire marker source is not digest-pinned: " + relative_path
            )

    for relative_path, expected in files.items():
        source = root / relative_path
        if not source.is_file():
            errors.append(f"NyxID wire source is missing: {relative_path}")
            continue
        if sha256(source) != expected:
            errors.append(f"NyxID wire source drift: {relative_path}")
        text = source.read_text(encoding="utf-8")
        for marker in required_markers.get(relative_path, []):
            if marker not in text:
                errors.append(
                    f"NyxID wire contract marker is missing from {relative_path}: {marker}"
                )
        for marker in forbidden_markers.get(relative_path, []):
            if marker in text:
                errors.append(
                    f"NyxID forbidden wire field appeared in {relative_path}: {marker}"
                )


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


def resolve_git_commit(root, revision, errors, label):
    result = subprocess.run(
        ["git", "rev-parse", "--verify", f"{revision}^{{commit}}"],
        cwd=root,
        text=True,
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        errors.append(f"cannot read {label}: {result.stderr.strip()[-200:]}")
        return None
    return result.stdout.strip()


def git_commit_exists(root, revision, errors, label):
    resolved = resolve_git_commit(root, revision, errors, label)
    if resolved is None:
        return False
    if resolved != revision:
        errors.append(f"{label} did not resolve to the declared commit SHA")
        return False
    return True


def git_is_ancestor(root, ancestor, descendant):
    result = subprocess.run(
        ["git", "merge-base", "--is-ancestor", ancestor, descendant],
        cwd=root,
        capture_output=True,
        check=False,
    )
    return result.returncode == 0


def git_blob(root, revision, relative_path, errors, label):
    result = subprocess.run(
        ["git", "cat-file", "blob", f"{revision}:{relative_path}"],
        cwd=root,
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        diagnostic = result.stderr.decode(errors="replace").strip()[-200:]
        errors.append(
            f"{label} is missing from {revision[:12]}: {relative_path}: {diagnostic}"
        )
        return None
    return result.stdout


if __name__ == "__main__":
    raise SystemExit(main())
