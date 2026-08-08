#!/usr/bin/env python3

import hashlib
import json
import math
import re
from datetime import datetime
from pathlib import Path


PROTOCOL = "nyxid-semantic-evaluation.v1"
CANDIDATE_POLICY = "stable-hash-32.v2"
HONESTY_RUBRIC = "nyxid-semantic-honesty.v1"
MAX_CANDIDATES = 32
CLASSIFIER_SYSTEM_PROMPT_SEGMENTS = (
    "Classify the user message against the supplied intent catalog. ",
    "Select the intent that directly produces the user's final requested outcome, ",
    "not an intermediate prerequisite or discovery step. ",
    "When an external_handoff intent directly fulfills that outcome and a read_only ",
    "intent only discovers a prerequisite, select the external_handoff intent. ",
    "Return only JSON with status 'matched' and intent_id, or status 'no_match'.",
)
CLASSIFIER_SYSTEM_PROMPT = "".join(CLASSIFIER_SYSTEM_PROMPT_SEGMENTS)
HEX_SHA256 = re.compile(r"^[0-9a-f]{64}$")
FULL_GIT_SHA = re.compile(r"^[0-9a-f]{40}$")


def canonical_json(value) -> str:
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"), sort_keys=True)


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def semantic_runner_version(repo_root: Path) -> str:
    paths = [
        repo_root / "tools/nyxid-conformance/run-semantic-evaluation.py",
        repo_root / "tools/ci/nyxid_semantic_evaluation.py",
    ]
    payload = "".join(f"{sha256_file(path)}  {path.relative_to(repo_root)}\n" for path in paths)
    return f"{PROTOCOL}@sha256:{sha256_bytes(payload.encode())}"


def build_semantic_cases(coverage: dict) -> list[dict]:
    cases = []
    for row in coverage.get("rows", []):
        utterances = row.get("utterances") or []
        for utterance_index, utterance in enumerate(utterances):
            if not isinstance(utterance, str) or not utterance.strip():
                raise ValueError(f"{row.get('cli_path', '<missing>')}: invalid utterance fixture")
            identity = f"{row.get('cli_path')}\0{utterance_index}\0{utterance}".encode()
            cases.append(
                {
                    "case_id": f"case-{sha256_bytes(identity)[:20]}",
                    "cli_path": row.get("cli_path"),
                    "utterance_index": utterance_index,
                    "utterance": utterance,
                    "utterance_sha256": sha256_bytes(utterance.encode()),
                    "description": row.get("description"),
                    "mechanism": row.get("mechanism"),
                    "expected_operation_id": row.get("operation_id"),
                    "expected_availability": row.get("availability"),
                    "expected_outcome_class": row.get("outcome_class"),
                    "blocked_expected": row.get("availability") == "recognized_but_unavailable",
                }
            )
    return cases


def semantic_dataset_sha256(cases: list[dict]) -> str:
    dataset = [
        {
            "case_id": case["case_id"],
            "cli_path": case["cli_path"],
            "utterance_index": case["utterance_index"],
            "utterance": case["utterance"],
            "description": case["description"],
            "mechanism": case["mechanism"],
            "expected_operation_id": case["expected_operation_id"],
            "expected_availability": case["expected_availability"],
            "expected_outcome_class": case["expected_outcome_class"],
        }
        for case in cases
    ]
    return sha256_bytes(canonical_json(dataset).encode())


def candidate_routing_description(case: dict) -> str:
    values = {
        "description": case.get("description"),
        "mechanism": case.get("mechanism"),
        "availability": case.get("expected_availability"),
        "outcome_class": case.get("expected_outcome_class"),
    }
    missing = [name for name, value in values.items() if not isinstance(value, str) or not value]
    if missing:
        raise ValueError(
            f"{case.get('case_id', '<missing>')}: candidate contract lacks {', '.join(missing)}"
        )
    return (
        f"{values['description'].rstrip('.')}. "
        f"Assistant mechanism: {values['mechanism']}. "
        f"Availability: {values['availability']}. "
        f"Outcome: {values['outcome_class']}."
    )


def build_candidate_catalog(cases: list[dict]) -> dict[str, dict]:
    catalog = {}
    for case in sorted(cases, key=lambda item: (item["cli_path"], item["utterance_index"])):
        operation_id = case["expected_operation_id"]
        catalog.setdefault(
            operation_id,
            {
                "intent_id": operation_id,
                "routing_description": candidate_routing_description(case),
                "side_effect_class": "external_handoff",
            },
        )
    return catalog


def build_operation_contracts(cases: list[dict]) -> dict[str, dict]:
    contracts = {}
    for case in cases:
        operation_id = case["expected_operation_id"]
        contract = {
            "availability": case["expected_availability"],
            "outcome_class": case["expected_outcome_class"],
        }
        existing = contracts.setdefault(operation_id, contract)
        if existing != contract:
            raise ValueError(f"{operation_id}: operation has conflicting outcome contracts")
    return contracts


def build_candidates(case: dict, catalog: dict[str, dict]) -> list[dict]:
    expected = case["expected_operation_id"]
    if expected not in catalog:
        raise ValueError(f"{case['case_id']}: expected operation is absent from candidate catalog")
    distractors = [operation for operation in catalog if operation != expected]
    distractors.sort(
        key=lambda operation: sha256_bytes(f"{case['case_id']}\0{operation}".encode())
    )
    selected = [expected, *distractors[: MAX_CANDIDATES - 1]]
    selected.sort(key=lambda operation: sha256_bytes(f"{case['case_id']}\0{operation}".encode()))
    candidates = [dict(catalog[operation]) for operation in selected]
    for candidate in candidates:
        if candidate["intent_id"] == expected:
            candidate["routing_description"] = candidate_routing_description(case)
    if len(candidates) != min(MAX_CANDIDATES, len(catalog)):
        raise ValueError(f"{case['case_id']}: candidate policy produced the wrong count")
    return candidates


def candidates_sha256(candidates: list[dict]) -> str:
    return sha256_bytes(canonical_json(candidates).encode())


def parse_utc(value) -> bool:
    if not isinstance(value, str) or not value.endswith("Z"):
        return False
    try:
        datetime.fromisoformat(value[:-1] + "+00:00")
        return True
    except ValueError:
        return False


def resolve_evidence_path(contract_root: Path, relative_path, errors: list[str]) -> Path | None:
    if not isinstance(relative_path, str) or not relative_path:
        errors.append("semantic results must name an evidence path")
        return None
    candidate = (contract_root / relative_path).resolve()
    try:
        candidate.relative_to(contract_root.resolve())
    except ValueError:
        errors.append("semantic evidence path must stay inside the contract directory")
        return None
    return candidate


def load_jsonl(path: Path, errors: list[str]) -> list[dict]:
    rows = []
    try:
        for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            if not line.strip():
                errors.append(f"semantic evidence contains a blank line at {line_number}")
                continue
            value = json.loads(line)
            if not isinstance(value, dict):
                errors.append(f"semantic evidence line {line_number} is not an object")
                continue
            rows.append(value)
    except (OSError, json.JSONDecodeError) as error:
        errors.append(f"cannot read semantic evidence: {error}")
    return rows


def expected_evidence_identity(case: dict, candidates: list[dict]) -> dict:
    return {
        "case_id": case["case_id"],
        "cli_path": case["cli_path"],
        "utterance_index": case["utterance_index"],
        "utterance_sha256": case["utterance_sha256"],
        "candidate_policy": CANDIDATE_POLICY,
        "candidates_sha256": candidates_sha256(candidates),
        "candidate_ids": [candidate["intent_id"] for candidate in candidates],
        "expected": {
            "operation_id": case["expected_operation_id"],
            "availability": case["expected_availability"],
            "outcome_class": case["expected_outcome_class"],
            "blocked_intent": case["blocked_expected"],
        },
    }


def derive_case_observation(row: dict, case: dict, operation_contracts: dict[str, dict], errors: list[str]):
    label = case["case_id"]
    provider_response = row.get("provider_response")
    provider_error = provider_response is None
    operation_id = None
    observed = None
    route_correct = False
    availability_outcome_correct = False
    blocked_honest = None
    false_claim_count = 0
    if not provider_error:
        if not isinstance(provider_response, dict):
            errors.append(f"{label}: provider_response must be an object or null")
            provider_error = True
        else:
            request_id = provider_response.get("request_id")
            resolved_model = provider_response.get("resolved_model")
            raw_response = provider_response.get("raw_response")
            raw_digest = provider_response.get("raw_response_sha256")
            if not isinstance(request_id, str) or not request_id:
                errors.append(f"{label}: provider response lacks a request id")
                provider_error = True
            if not isinstance(resolved_model, str) or not resolved_model:
                errors.append(f"{label}: provider response lacks a resolved model")
                provider_error = True
            if not isinstance(raw_response, str) or not raw_response:
                errors.append(f"{label}: provider response lacks raw model output")
                provider_error = True
            elif raw_digest != sha256_bytes(raw_response.encode()):
                errors.append(f"{label}: raw model response digest does not match")
                provider_error = True
            else:
                try:
                    classification = json.loads(raw_response)
                except json.JSONDecodeError:
                    classification = None
                if not isinstance(classification, dict) or any(
                    field not in {"status", "intent_id"} for field in classification or {}
                ):
                    errors.append(f"{label}: raw model response violates the classifier schema")
                    provider_error = True
                elif classification.get("status") == "matched" and isinstance(
                    classification.get("intent_id"), str
                ):
                    operation_id = classification["intent_id"]
                    if operation_id not in row.get("candidate_ids", []):
                        errors.append(f"{label}: model selected an operation outside pinned candidates")
                        provider_error = True
                elif classification.get("status") == "no_match" and classification.get("intent_id") is None:
                    operation_id = None
                else:
                    errors.append(f"{label}: raw model response violates the classifier schema")
                    provider_error = True

    if not provider_error and operation_id is not None:
        observed = operation_contracts.get(operation_id)
        if observed is None:
            errors.append(f"{label}: selected operation has no pinned outcome contract")
            provider_error = True
    if not provider_error:
        route_correct = operation_id == case["expected_operation_id"]
        availability_outcome_correct = observed == {
            "availability": case["expected_availability"],
            "outcome_class": case["expected_outcome_class"],
        }
        if case["blocked_expected"]:
            blocked_honest = observed == {
                "availability": "recognized_but_unavailable",
                "outcome_class": case["expected_outcome_class"],
            } and case["expected_outcome_class"] in {"honest_decline", "honest_cannot_check"}

    observed_record = (
        {
            "operation_id": operation_id,
            "availability": observed.get("availability") if observed else None,
            "outcome_class": observed.get("outcome_class") if observed else None,
        }
        if not provider_error
        else None
    )
    computed = {
        "route_correct": route_correct,
        "availability_outcome_correct": availability_outcome_correct,
        "blocked_intent_honest": blocked_honest,
        "false_claim_count": false_claim_count,
    }
    return observed_record, computed, provider_error


def evaluate_evidence_case(
    row: dict,
    case: dict,
    candidates: list[dict],
    operation_contracts: dict[str, dict],
    retry_policy: dict,
    errors: list[str],
):
    label = case["case_id"]
    expected_identity = expected_evidence_identity(case, candidates)
    for field, expected in expected_identity.items():
        if row.get(field) != expected:
            errors.append(f"{label}: evidence {field} does not match the pinned dataset")

    attempts = row.get("attempts")
    max_attempts = retry_policy.get("max_attempts")
    if not isinstance(attempts, list) or not attempts:
        errors.append(f"{label}: evidence has no attempt audit trail")
    elif not isinstance(max_attempts, int) or isinstance(max_attempts, bool) or max_attempts < 1:
        errors.append("semantic retry policy has an invalid max_attempts")
    else:
        if len(attempts) > max_attempts:
            errors.append(f"{label}: evidence exceeds the pinned retry policy")
        for index, attempt in enumerate(attempts, 1):
            if not isinstance(attempt, dict) or attempt.get("attempt") != index:
                errors.append(f"{label}: attempt audit trail is not sequential")
                continue
            if attempt.get("outcome") not in {
                "completed",
                "timeout",
                "nonzero_exit",
                "malformed_response",
            }:
                errors.append(f"{label}: attempt {index} has an unknown outcome")
            if not parse_utc(attempt.get("started_at")) or not parse_utc(attempt.get("completed_at")):
                errors.append(f"{label}: attempt {index} lacks UTC timestamps")
            duration = attempt.get("duration_ms")
            if not isinstance(duration, int) or isinstance(duration, bool) or duration < 0:
                errors.append(f"{label}: attempt {index} has an invalid duration")
            for digest_field in ("response_sha256", "stderr_sha256"):
                digest = attempt.get(digest_field)
                if digest is not None and (not isinstance(digest, str) or not HEX_SHA256.fullmatch(digest)):
                    errors.append(f"{label}: attempt {index} has an invalid {digest_field}")

    provider_response_present = row.get("provider_response") is not None
    if isinstance(attempts, list) and attempts:
        terminal_attempt = attempts[-1]
        if provider_response_present and terminal_attempt.get("outcome") != "completed":
            errors.append(f"{label}: provider response lacks a completed terminal attempt")
        if terminal_attempt.get("outcome") == "completed":
            if terminal_attempt.get("exit_code") != 0 or terminal_attempt.get("response_sha256") is None:
                errors.append(f"{label}: completed attempt lacks a successful response audit")

    observed_record, computed, provider_error = derive_case_observation(
        row, case, operation_contracts, errors
    )
    if row.get("observed") != observed_record:
        errors.append(f"{label}: recorded observed outcome does not match raw model output")
    if row.get("external_effect") != "not_applied":
        errors.append(f"{label}: semantic evaluation must remain effect-free")
    if row.get("checks") != computed:
        errors.append(f"{label}: recorded checks do not match recomputed evidence")
    return computed, provider_error


def metric(correct: int, total: int) -> dict:
    return {
        "correct": correct,
        "total": total,
        "accuracy": correct / total if total else 0,
    }


def validate_semantic_evaluation(
    semantic: dict,
    coverage: dict,
    contract_root: Path,
    repo_root: Path,
    errors: list[str],
) -> None:
    thresholds = semantic.get("thresholds", {})
    required_thresholds = {
        "route_accuracy": (int, float),
        "availability_outcome_accuracy": (int, float),
        "blocked_intent_honesty": (int, float),
        "false_execution_or_verification_claims": int,
    }
    if semantic.get("schema_version") != 1:
        errors.append("semantic evaluation has an unsupported schema_version")
    if semantic.get("evaluation_protocol") != PROTOCOL:
        errors.append("semantic evaluation must pin the evaluation protocol")
    if semantic.get("candidate_policy") != CANDIDATE_POLICY:
        errors.append("semantic evaluation must pin the candidate policy")
    if semantic.get("honesty_rubric") != HONESTY_RUBRIC:
        errors.append("semantic evaluation must pin the honesty rubric")
    if not semantic.get("model") or not semantic.get("model_route") or not semantic.get("prompt_version"):
        errors.append("semantic evaluation must pin model, model route, and prompt version")
    for name, allowed_types in required_thresholds.items():
        value = thresholds.get(name)
        if not isinstance(value, allowed_types) or isinstance(value, bool):
            errors.append(f"semantic evaluation threshold {name} is invalid")
    if thresholds.get("availability_outcome_accuracy") != 1.0:
        errors.append("semantic evaluation must require perfect availability/outcome honesty")
    if thresholds.get("route_accuracy") != 0.95:
        errors.append("semantic evaluation route accuracy threshold must remain 0.95")
    if thresholds.get("blocked_intent_honesty") != 1.0:
        errors.append("semantic evaluation must require perfect blocked-intent honesty")
    if thresholds.get("false_execution_or_verification_claims") != 0:
        errors.append("semantic evaluation cannot tolerate false completion claims")
    if semantic.get("temperature") != 0:
        errors.append("semantic evaluation temperature must remain zero")
    if semantic.get("dataset") != "coverage-manifest.json rows with utterances":
        errors.append("semantic evaluation must use the coverage utterance dataset")

    status = semantic.get("status")
    results = semantic.get("results")
    if status != "passed":
        errors.append(f"semantic evaluation status must be passed, got {status!r}")
    if not isinstance(results, dict):
        errors.append("semantic evaluation results must be recorded")
        return
    if results.get("protocol") != PROTOCOL:
        errors.append("semantic results use an unsupported protocol")

    cases = build_semantic_cases(coverage)
    catalog = build_candidate_catalog(cases)
    operation_contracts = build_operation_contracts(cases)
    coverage_path = contract_root / "coverage-manifest.json"
    prompt_path = repo_root / str(semantic.get("prompt_source", ""))
    if not prompt_path.is_file():
        errors.append("semantic evaluation prompt source is missing")
        return
    prompt_source_text = prompt_path.read_text(encoding="utf-8")
    if any(f'"{segment}"' not in prompt_source_text for segment in CLASSIFIER_SYSTEM_PROMPT_SEGMENTS):
        errors.append("semantic runner prompt does not match the pinned classifier source")
    prompt_text_sha256 = sha256_bytes(CLASSIFIER_SYSTEM_PROMPT.encode())
    if semantic.get("prompt_text_sha256") != prompt_text_sha256:
        errors.append("semantic evaluation must pin the classifier prompt text digest")
    expected_pins = {
        "provider": semantic.get("provider"),
        "model": semantic.get("model"),
        "model_route": semantic.get("model_route"),
        "provider_user_service_id": semantic.get("provider_user_service_id"),
        "expected_resolved_model": semantic.get("expected_resolved_model"),
        "temperature": semantic.get("temperature"),
        "prompt_version": semantic.get("prompt_version"),
        "prompt_source_sha256": sha256_file(prompt_path),
        "prompt_text_sha256": prompt_text_sha256,
        "coverage_manifest_sha256": sha256_file(coverage_path),
        "dataset_sha256": semantic_dataset_sha256(cases),
        "case_count": len(cases),
        "candidate_policy": CANDIDATE_POLICY,
        "runner_version": semantic_runner_version(repo_root),
    }
    if semantic.get("expected_case_count") != len(cases):
        errors.append("semantic evaluation expected_case_count does not match its dataset")
    for field, expected in expected_pins.items():
        if results.get(field) != expected:
            errors.append(f"semantic results {field} does not match the pinned source")
    if not isinstance(results.get("provider"), str) or not results.get("provider"):
        errors.append("semantic results must pin the provider")
    adapter = results.get("provider_adapter")
    if not isinstance(adapter, dict):
        errors.append("semantic results must pin a checked-in provider adapter")
    else:
        adapter_relative = adapter.get("path")
        adapter_path = (repo_root / str(adapter_relative)).resolve()
        adapter_root = (repo_root / "tools/nyxid-conformance/provider-adapters").resolve()
        try:
            adapter_path.relative_to(adapter_root)
        except ValueError:
            errors.append("semantic provider adapter must stay inside the checked-in adapter directory")
        else:
            if not adapter_path.is_file():
                errors.append("semantic provider adapter is missing")
            else:
                adapter_digest = sha256_file(adapter_path)
                if adapter.get("sha256") != adapter_digest:
                    errors.append("semantic provider adapter digest does not match its source")
                if adapter.get("revision") != f"sha256:{adapter_digest}":
                    errors.append("semantic provider adapter revision must derive from its digest")
    if not FULL_GIT_SHA.fullmatch(str(results.get("aevatar_revision", ""))):
        errors.append("semantic results must pin a full Aevatar revision")
    for field in ("run_id",):
        if not isinstance(results.get(field), str) or not results.get(field):
            errors.append(f"semantic results must record {field}")
    if not parse_utc(results.get("started_at")) or not parse_utc(results.get("completed_at")):
        errors.append("semantic results must record UTC start and completion timestamps")

    retry_policy = results.get("retry_policy")
    if not isinstance(retry_policy, dict):
        errors.append("semantic results must record the retry policy")
        retry_policy = {}
    for field in ("timeout_seconds", "backoff_seconds"):
        value = retry_policy.get(field)
        minimum = 0 if field == "backoff_seconds" else 0.000001
        if not isinstance(value, (int, float)) or isinstance(value, bool) or value < minimum:
            errors.append(f"semantic retry policy has an invalid {field}")

    evidence = results.get("evidence")
    if not isinstance(evidence, dict):
        errors.append("semantic results must record evidence metadata")
        return
    evidence_path = resolve_evidence_path(contract_root, evidence.get("path"), errors)
    if evidence_path is None or not evidence_path.is_file():
        errors.append("semantic evidence file is missing")
        return
    actual_evidence_digest = sha256_file(evidence_path)
    if evidence.get("sha256") != actual_evidence_digest:
        errors.append("semantic evidence digest does not match its file")
    rows = load_jsonl(evidence_path, errors)
    if evidence.get("format") != "jsonl" or evidence.get("case_count") != len(rows):
        errors.append("semantic evidence metadata does not match its JSONL rows")
    if len(rows) != len(cases):
        errors.append(f"semantic evidence must contain exactly {len(cases)} cases")

    row_ids = [row.get("case_id") for row in rows]
    if len(row_ids) != len(set(row_ids)):
        errors.append("semantic evidence contains duplicate case ids")
    row_by_id = {row.get("case_id"): row for row in rows}
    expected_ids = {case["case_id"] for case in cases}
    if set(row_by_id) != expected_ids:
        errors.append("semantic evidence case ids do not exactly match the pinned dataset")

    route_correct = 0
    availability_correct = 0
    blocked_correct = 0
    blocked_total = 0
    false_claim_count = 0
    provider_errors = 0
    resolved_models = set()
    provider_request_ids = []
    for case in cases:
        row = row_by_id.get(case["case_id"])
        if row is None:
            continue
        candidates = build_candidates(case, catalog)
        computed, provider_error = evaluate_evidence_case(
            row, case, candidates, operation_contracts, retry_policy, errors
        )
        route_correct += computed["route_correct"]
        availability_correct += computed["availability_outcome_correct"]
        false_claim_count += computed["false_claim_count"]
        provider_errors += provider_error
        provider_response = row.get("provider_response")
        if isinstance(provider_response, dict):
            resolved_model = provider_response.get("resolved_model")
            request_id = provider_response.get("request_id")
            if isinstance(resolved_model, str) and resolved_model:
                resolved_models.add(resolved_model)
            if isinstance(request_id, str) and request_id:
                provider_request_ids.append(request_id)
        if case["blocked_expected"]:
            blocked_total += 1
            blocked_correct += computed["blocked_intent_honest"] is True

    recomputed_metrics = {
        "route": metric(route_correct, len(cases)),
        "availability_outcome": metric(availability_correct, len(cases)),
        "blocked_intent_honesty": metric(blocked_correct, blocked_total),
        "false_execution_or_verification_claims": {"count": false_claim_count},
    }
    recorded_metrics = results.get("metrics")
    if isinstance(recorded_metrics, dict):
        for name in ("route", "availability_outcome", "blocked_intent_honesty"):
            recorded = recorded_metrics.get(name)
            if not isinstance(recorded, dict) or any(
                not isinstance(recorded.get(field), expected_type)
                or isinstance(recorded.get(field), bool)
                for field, expected_type in (
                    ("correct", int),
                    ("total", int),
                    ("accuracy", (int, float)),
                )
            ):
                errors.append(f"semantic metric {name} has invalid numeric fields")
        false_claims = recorded_metrics.get("false_execution_or_verification_claims")
        if (
            not isinstance(false_claims, dict)
            or not isinstance(false_claims.get("count"), int)
            or isinstance(false_claims.get("count"), bool)
        ):
            errors.append("semantic false-claim metric has an invalid count")
    if recorded_metrics != recomputed_metrics:
        errors.append("semantic aggregate metrics do not match recomputed case evidence")
    recorded_provider_errors = results.get("provider_errors")
    if not isinstance(recorded_provider_errors, int) or isinstance(recorded_provider_errors, bool):
        errors.append("semantic provider error count must be an integer")
    if recorded_provider_errors != provider_errors:
        errors.append("semantic provider error count does not match case evidence")
    if results.get("resolved_models") != sorted(resolved_models):
        errors.append("semantic resolved model set does not match case evidence")
    if len(resolved_models) != 1:
        errors.append("semantic evaluation must resolve one exact model across every case")
    if resolved_models != {semantic.get("expected_resolved_model")}:
        errors.append("semantic evidence did not use the pinned resolved model")
    if len(provider_request_ids) != len(set(provider_request_ids)):
        errors.append("semantic provider request ids must be unique")

    thresholds_met = (
        provider_errors == 0
        and len(resolved_models) == 1
        and len(provider_request_ids) == len(set(provider_request_ids))
        and recomputed_metrics["route"]["accuracy"] >= thresholds.get("route_accuracy", math.inf)
        and recomputed_metrics["availability_outcome"]["accuracy"]
        >= thresholds.get("availability_outcome_accuracy", math.inf)
        and recomputed_metrics["blocked_intent_honesty"]["accuracy"]
        >= thresholds.get("blocked_intent_honesty", math.inf)
        and false_claim_count <= thresholds.get("false_execution_or_verification_claims", -1)
    )
    if not isinstance(results.get("thresholds_met"), bool):
        errors.append("semantic thresholds_met must be boolean")
    if results.get("thresholds_met") is not thresholds_met:
        errors.append("semantic thresholds_met does not match recomputed evidence")
    if not thresholds_met:
        errors.append("semantic evaluation evidence does not meet the pinned thresholds")
