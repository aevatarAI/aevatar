#!/usr/bin/env python3

import argparse
import json
import os
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
CI_ROOT = REPO_ROOT / "tools/ci"
sys.path.insert(0, str(CI_ROOT))

from nyxid_semantic_evaluation import (  # noqa: E402
    CANDIDATE_POLICY,
    CLASSIFIER_SYSTEM_PROMPT,
    HONESTY_RUBRIC,
    PROTOCOL,
    build_candidate_catalog,
    build_candidates,
    build_operation_contracts,
    build_semantic_cases,
    candidates_sha256,
    canonical_json,
    derive_case_observation,
    expected_evidence_identity,
    metric,
    semantic_dataset_sha256,
    semantic_runner_version,
    sha256_bytes,
    sha256_file,
)


CONTRACT_ROOT = REPO_ROOT / "docs/contracts/nyxid-assistant-conformance/v1"
DEFAULT_EVALUATION = CONTRACT_ROOT / "semantic-evaluation.json"
DEFAULT_COVERAGE = CONTRACT_ROOT / "coverage-manifest.json"


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def load_json(path: Path):
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def atomic_write(path: Path, payload: str, overwrite: bool) -> None:
    if path.exists() and not overwrite:
        raise ValueError(f"refusing to overwrite existing output: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary_path = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        temporary_path.replace(path)
    except Exception:
        temporary_path.unlink(missing_ok=True)
        raise


def validate_provider_response(response: dict, request: dict) -> str | None:
    if response.get("schema_version") != 1:
        return "provider response has an unsupported schema_version"
    if not isinstance(response.get("request_id"), str) or not response["request_id"]:
        return "provider response lacks request_id"
    if not isinstance(response.get("resolved_model"), str) or not response["resolved_model"]:
        return "provider response lacks resolved_model"
    if not isinstance(response.get("raw_response"), str) or not response["raw_response"]:
        return "provider response lacks raw_response"
    return None


def invoke_provider(argv: list[str], request: dict, timeout_seconds: float):
    started_at = utc_now()
    started = time.monotonic()
    try:
        result = subprocess.run(
            argv,
            input=canonical_json(request) + "\n",
            text=True,
            capture_output=True,
            timeout=timeout_seconds,
            check=False,
        )
    except subprocess.TimeoutExpired as error:
        completed_at = utc_now()
        stderr = error.stderr.encode() if isinstance(error.stderr, str) else error.stderr or b""
        return None, {
            "attempt": request["attempt"],
            "started_at": started_at,
            "completed_at": completed_at,
            "duration_ms": round((time.monotonic() - started) * 1000),
            "outcome": "timeout",
            "exit_code": None,
            "response_sha256": None,
            "stderr_sha256": sha256_bytes(stderr),
            "error": "provider_timeout",
        }

    stdout = result.stdout.encode()
    stderr = result.stderr.encode()
    audit = {
        "attempt": request["attempt"],
        "started_at": started_at,
        "completed_at": utc_now(),
        "duration_ms": round((time.monotonic() - started) * 1000),
        "outcome": "completed",
        "exit_code": result.returncode,
        "response_sha256": sha256_bytes(stdout) if stdout else None,
        "stderr_sha256": sha256_bytes(stderr),
        "error": None,
    }
    if result.returncode != 0:
        audit["outcome"] = "nonzero_exit"
        audit["error"] = "provider_nonzero_exit"
        return None, audit
    try:
        response = json.loads(result.stdout)
    except json.JSONDecodeError:
        audit["outcome"] = "malformed_response"
        audit["error"] = "provider_response_not_json"
        return None, audit
    if not isinstance(response, dict):
        audit["outcome"] = "malformed_response"
        audit["error"] = "provider_response_not_object"
        return None, audit
    response_error = validate_provider_response(response, request)
    if response_error:
        audit["outcome"] = "malformed_response"
        audit["error"] = response_error
        return None, audit
    return response, audit


def evidence_for_case(
    case: dict,
    candidates: list[dict],
    operation_contracts: dict[str, dict],
    response: dict | None,
    attempts: list[dict],
):
    row = expected_evidence_identity(case, candidates)
    row["attempts"] = attempts
    if response is None:
        row["provider_response"] = None
    else:
        raw_response = response["raw_response"]
        row["provider_response"] = {
            "request_id": response["request_id"],
            "resolved_model": response["resolved_model"],
            "raw_response": raw_response,
            "raw_response_sha256": sha256_bytes(raw_response.encode()),
        }
    row["external_effect"] = "not_applied"
    derivation_errors = []
    observed, checks, _ = derive_case_observation(
        row, case, operation_contracts, derivation_errors
    )
    row["observed"] = observed
    row["checks"] = checks
    return row


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Run the pinned NyxID semantic dataset through an effect-free provider adapter."
    )
    parser.add_argument("--evaluation", type=Path, default=DEFAULT_EVALUATION)
    parser.add_argument("--coverage", type=Path, default=DEFAULT_COVERAGE)
    parser.add_argument("--provider", required=True)
    parser.add_argument("--provider-route", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--temperature", required=True, type=float)
    parser.add_argument("--prompt-version", required=True)
    parser.add_argument("--provider-adapter", required=True, type=Path)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--aevatar-revision", required=True)
    parser.add_argument("--timeout-seconds", type=float, default=60)
    parser.add_argument("--max-attempts", type=int, default=1)
    parser.add_argument("--retry-backoff-seconds", type=float, default=0)
    parser.add_argument("--evidence-output", required=True, type=Path)
    parser.add_argument("--evaluation-output", required=True, type=Path)
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()

    evaluation_path = args.evaluation.resolve()
    coverage_path = args.coverage.resolve()
    evidence_path = args.evidence_output.resolve()
    output_path = args.evaluation_output.resolve()
    if output_path == evaluation_path:
        parser.error("evaluation-output must not overwrite the authoritative evaluation config")
    if not args.overwrite:
        for path in (evidence_path, output_path):
            if path.exists():
                parser.error(f"refusing to overwrite existing output: {path}")
    try:
        evidence_relative = evidence_path.relative_to(evaluation_path.parent)
    except ValueError:
        parser.error("evidence-output must be inside the evaluation contract directory")
    if args.max_attempts < 1 or args.timeout_seconds <= 0 or args.retry_backoff_seconds < 0:
        parser.error("retry and timeout values are out of bounds")

    semantic = load_json(evaluation_path)
    coverage = load_json(coverage_path)
    pinned = {
        "provider": semantic.get("provider"),
        "provider_route": semantic.get("model_route"),
        "model": semantic.get("model"),
        "temperature": semantic.get("temperature"),
        "prompt_version": semantic.get("prompt_version"),
    }
    supplied = {
        "provider": args.provider,
        "provider_route": args.provider_route,
        "model": args.model,
        "temperature": args.temperature,
        "prompt_version": args.prompt_version,
    }
    for field, value in supplied.items():
        if value != pinned[field]:
            parser.error(f"{field} does not match semantic-evaluation.json")
    if semantic.get("evaluation_protocol") != PROTOCOL:
        parser.error("semantic-evaluation.json does not pin the runner protocol")
    if semantic.get("candidate_policy") != CANDIDATE_POLICY:
        parser.error("semantic-evaluation.json does not pin the candidate policy")
    if semantic.get("honesty_rubric") != HONESTY_RUBRIC:
        parser.error("semantic-evaluation.json does not pin the honesty rubric")
    if semantic.get("prompt_text_sha256") != sha256_bytes(CLASSIFIER_SYSTEM_PROMPT.encode()):
        parser.error("semantic-evaluation.json does not pin the classifier prompt text")

    head = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=REPO_ROOT,
        text=True,
        capture_output=True,
        check=True,
    ).stdout.strip()
    if args.aevatar_revision != head:
        parser.error(f"aevatar-revision must equal the checked-out HEAD {head}")

    cases = build_semantic_cases(coverage)
    if semantic.get("expected_case_count") != len(cases):
        parser.error("coverage utterance count does not match expected_case_count")
    catalog = build_candidate_catalog(cases)
    prompt_path = REPO_ROOT / semantic["prompt_source"]
    if not prompt_path.is_file():
        parser.error(f"prompt source is missing: {prompt_path}")
    adapter_path = args.provider_adapter.resolve()
    adapter_root = (REPO_ROOT / "tools/nyxid-conformance/provider-adapters").resolve()
    try:
        adapter_path.relative_to(adapter_root)
    except ValueError:
        parser.error("provider-adapter must stay inside the checked-in adapter directory")
    if not adapter_path.is_file() or adapter_path.suffix != ".py":
        parser.error("provider-adapter must be a checked-in Python adapter")
    adapter_digest = sha256_file(adapter_path)
    argv = [sys.executable, str(adapter_path)]
    operation_contracts = build_operation_contracts(cases)

    run_started_at = utc_now()
    rows = []
    for case in cases:
        candidates = build_candidates(case, catalog)
        attempts = []
        response = None
        for attempt in range(1, args.max_attempts + 1):
            request = {
                "schema_version": 1,
                "protocol": PROTOCOL,
                "run_id": args.run_id,
                "case_id": case["case_id"],
                "utterance": case["utterance"],
                "candidates": candidates,
                "candidate_ids": [candidate["intent_id"] for candidate in candidates],
                "candidate_policy": CANDIDATE_POLICY,
                "candidates_sha256": candidates_sha256(candidates),
                "provider": args.provider,
                "model": args.model,
                "model_route": args.provider_route,
                "provider_user_service_id": semantic["provider_user_service_id"],
                "temperature": args.temperature,
                "prompt_version": args.prompt_version,
                "prompt_source": semantic["prompt_source"],
                "prompt_source_sha256": sha256_file(prompt_path),
                "attempt": attempt,
                "timeout_seconds": args.timeout_seconds,
                "effect_policy": "forbid",
                "llm_request": {
                    "messages": [
                        {"role": "system", "content": CLASSIFIER_SYSTEM_PROMPT},
                        {
                            "role": "user",
                            "content": json.dumps(
                                {
                                    "user_message": case["utterance"],
                                    "intents": candidates,
                                },
                                ensure_ascii=False,
                                separators=(",", ":"),
                            ),
                        },
                    ],
                    "tools": None,
                    "response_format": "agent_profile_turn_classification",
                    "max_tokens": 128,
                    "temperature": args.temperature,
                },
            }
            response, audit = invoke_provider(argv, request, args.timeout_seconds)
            attempts.append(audit)
            if response is not None:
                break
            if attempt < args.max_attempts and args.retry_backoff_seconds:
                time.sleep(args.retry_backoff_seconds)
        rows.append(evidence_for_case(case, candidates, operation_contracts, response, attempts))

    evidence_payload = "".join(canonical_json(row) + "\n" for row in rows)
    route_correct = sum(row["checks"]["route_correct"] for row in rows)
    availability_correct = sum(row["checks"]["availability_outcome_correct"] for row in rows)
    blocked_rows = [row for row in rows if row["expected"]["blocked_intent"]]
    blocked_correct = sum(row["checks"]["blocked_intent_honest"] is True for row in blocked_rows)
    false_claim_count = sum(row["checks"]["false_claim_count"] for row in rows)
    provider_errors = 0
    derivation_errors = []
    resolved_models = set()
    provider_request_ids = []
    for case, row in zip(cases, rows, strict=True):
        _, _, provider_error = derive_case_observation(
            row, case, operation_contracts, derivation_errors
        )
        provider_errors += provider_error
        provider_response = row.get("provider_response")
        if isinstance(provider_response, dict):
            resolved_models.add(provider_response["resolved_model"])
            provider_request_ids.append(provider_response["request_id"])
    metrics = {
        "route": metric(route_correct, len(rows)),
        "availability_outcome": metric(availability_correct, len(rows)),
        "blocked_intent_honesty": metric(blocked_correct, len(blocked_rows)),
        "false_execution_or_verification_claims": {"count": false_claim_count},
    }
    thresholds = semantic["thresholds"]
    thresholds_met = (
        provider_errors == 0
        and len(resolved_models) == 1
        and len(provider_request_ids) == len(set(provider_request_ids))
        and metrics["route"]["accuracy"] >= thresholds["route_accuracy"]
        and metrics["availability_outcome"]["accuracy"]
        >= thresholds["availability_outcome_accuracy"]
        and metrics["blocked_intent_honesty"]["accuracy"] >= thresholds["blocked_intent_honesty"]
        and false_claim_count <= thresholds["false_execution_or_verification_claims"]
    )
    results = {
        "protocol": PROTOCOL,
        "run_id": args.run_id,
        "started_at": run_started_at,
        "completed_at": utc_now(),
        "aevatar_revision": args.aevatar_revision,
        "provider": args.provider,
        "provider_adapter": {
            "path": adapter_path.relative_to(REPO_ROOT).as_posix(),
            "sha256": adapter_digest,
            "revision": f"sha256:{adapter_digest}",
        },
        "model": args.model,
        "model_route": args.provider_route,
        "provider_user_service_id": semantic["provider_user_service_id"],
        "expected_resolved_model": semantic["expected_resolved_model"],
        "temperature": args.temperature,
        "prompt_version": args.prompt_version,
        "prompt_source_sha256": sha256_file(prompt_path),
        "prompt_text_sha256": sha256_bytes(CLASSIFIER_SYSTEM_PROMPT.encode()),
        "coverage_manifest_sha256": sha256_file(coverage_path),
        "dataset_sha256": semantic_dataset_sha256(cases),
        "case_count": len(cases),
        "candidate_policy": CANDIDATE_POLICY,
        "runner_version": semantic_runner_version(REPO_ROOT),
        "retry_policy": {
            "timeout_seconds": args.timeout_seconds,
            "max_attempts": args.max_attempts,
            "backoff_seconds": args.retry_backoff_seconds,
        },
        "evidence": {
            "path": evidence_relative.as_posix(),
            "format": "jsonl",
            "sha256": sha256_bytes(evidence_payload.encode()),
            "case_count": len(rows),
        },
        "metrics": metrics,
        "provider_errors": provider_errors,
        "resolved_models": sorted(resolved_models),
        "thresholds_met": thresholds_met,
    }
    candidate_evaluation = dict(semantic)
    candidate_evaluation["status"] = "passed" if thresholds_met else "failed"
    candidate_evaluation["results"] = results

    atomic_write(evidence_path, evidence_payload, args.overwrite)
    atomic_write(output_path, json.dumps(candidate_evaluation, indent=2) + "\n", args.overwrite)
    print(
        f"NyxID semantic evaluation {candidate_evaluation['status']}: "
        f"route={route_correct}/{len(rows)}, availability/outcome={availability_correct}/{len(rows)}, "
        f"blocked-honesty={blocked_correct}/{len(blocked_rows)}, false-claims={false_claim_count}, "
        f"provider-errors={provider_errors}."
    )
    print(f"Candidate evaluation: {output_path}")
    print(f"Case evidence: {evidence_path}")
    return 0 if thresholds_met else 1


if __name__ == "__main__":
    raise SystemExit(main())
