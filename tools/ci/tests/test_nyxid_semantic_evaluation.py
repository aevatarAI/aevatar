#!/usr/bin/env python3

import copy
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[3]
CI_ROOT = REPO_ROOT / "tools/ci"
sys.path.insert(0, str(CI_ROOT))

from nyxid_semantic_evaluation import (  # noqa: E402
    CANDIDATE_POLICY,
    CLASSIFIER_SYSTEM_PROMPT,
    PROTOCOL,
    build_candidate_catalog,
    build_candidates,
    build_operation_contracts,
    build_semantic_cases,
    candidate_routing_description,
    canonical_json,
    derive_case_observation,
    expected_evidence_identity,
    metric,
    semantic_dataset_sha256,
    semantic_runner_version,
    sha256_bytes,
    sha256_file,
    validate_semantic_evaluation,
)


CONTRACT_ROOT = REPO_ROOT / "docs/contracts/nyxid-assistant-conformance/v1"
ADAPTER_PATH = REPO_ROOT / "tools/nyxid-conformance/provider-adapters/nyxid-proxy-chat-completions.py"
RUNNER_PATH = REPO_ROOT / "tools/nyxid-conformance/run-semantic-evaluation.py"


def load_json(path: Path):
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def passing_fixture(contract_root: Path):
    coverage_source = CONTRACT_ROOT / "coverage-manifest.json"
    coverage_path = contract_root / "coverage-manifest.json"
    coverage_path.write_bytes(coverage_source.read_bytes())
    coverage = load_json(coverage_path)
    semantic = load_json(CONTRACT_ROOT / "semantic-evaluation.json")
    cases = build_semantic_cases(coverage)
    catalog = build_candidate_catalog(cases)
    operation_contracts = build_operation_contracts(cases)
    attempts = [
        {
            "attempt": 1,
            "started_at": "2026-08-09T00:00:00.000Z",
            "completed_at": "2026-08-09T00:00:00.001Z",
            "duration_ms": 1,
            "outcome": "completed",
            "exit_code": 0,
            "response_sha256": "a" * 64,
            "stderr_sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "error": None,
        }
    ]
    rows = []
    for index, case in enumerate(cases):
        candidates = build_candidates(case, catalog)
        raw_response = canonical_json(
            {"status": "matched", "intent_id": case["expected_operation_id"]}
        )
        row = expected_evidence_identity(case, candidates)
        row["attempts"] = copy.deepcopy(attempts)
        row["provider_response"] = {
            "request_id": f"request-{index}",
            "resolved_model": semantic["expected_resolved_model"],
            "raw_response": raw_response,
            "raw_response_sha256": sha256_bytes(raw_response.encode()),
        }
        row["external_effect"] = "not_applied"
        derivation_errors = []
        observed, checks, provider_error = derive_case_observation(
            row, case, operation_contracts, derivation_errors
        )
        if derivation_errors or provider_error:
            raise AssertionError(derivation_errors)
        row["observed"] = observed
        row["checks"] = checks
        rows.append(row)

    evidence_path = contract_root / "evidence/test-run.jsonl"
    evidence_path.parent.mkdir(parents=True)
    evidence_payload = "".join(canonical_json(row) + "\n" for row in rows)
    evidence_path.write_text(evidence_payload, encoding="utf-8")
    blocked = [row for row in rows if row["expected"]["blocked_intent"]]
    metrics = {
        "route": metric(len(rows), len(rows)),
        "availability_outcome": metric(len(rows), len(rows)),
        "blocked_intent_honesty": metric(len(blocked), len(blocked)),
        "false_execution_or_verification_claims": {"count": 0},
    }
    adapter_digest = sha256_file(ADAPTER_PATH)
    semantic["status"] = "passed"
    semantic["results"] = {
        "protocol": PROTOCOL,
        "run_id": "semantic-test-run",
        "started_at": "2026-08-09T00:00:00.000Z",
        "completed_at": "2026-08-09T00:01:00.000Z",
        "aevatar_revision": "87daa99e641533f25ea0ddc67396e1a0dc52bd59",
        "provider": semantic["provider"],
        "provider_adapter": {
            "path": ADAPTER_PATH.relative_to(REPO_ROOT).as_posix(),
            "sha256": adapter_digest,
            "revision": f"sha256:{adapter_digest}",
        },
        "model": semantic["model"],
        "model_route": semantic["model_route"],
        "provider_user_service_id": semantic["provider_user_service_id"],
        "expected_resolved_model": semantic["expected_resolved_model"],
        "temperature": semantic["temperature"],
        "prompt_version": semantic["prompt_version"],
        "prompt_source_sha256": sha256_file(REPO_ROOT / semantic["prompt_source"]),
        "prompt_text_sha256": sha256_bytes(CLASSIFIER_SYSTEM_PROMPT.encode()),
        "coverage_manifest_sha256": sha256_file(coverage_path),
        "dataset_sha256": semantic_dataset_sha256(cases),
        "case_count": len(cases),
        "candidate_policy": CANDIDATE_POLICY,
        "runner_version": semantic_runner_version(REPO_ROOT),
        "retry_policy": {"timeout_seconds": 60, "max_attempts": 1, "backoff_seconds": 0},
        "evidence": {
            "path": "evidence/test-run.jsonl",
            "format": "jsonl",
            "sha256": sha256_bytes(evidence_payload.encode()),
            "case_count": len(rows),
        },
        "metrics": metrics,
        "provider_errors": 0,
        "resolved_models": [semantic["expected_resolved_model"]],
        "thresholds_met": True,
    }
    return semantic, coverage, rows, evidence_path


class NyxIdSemanticEvaluationTests(unittest.TestCase):
    def test_pinned_dataset_has_134_unique_cases_and_32_candidates(self):
        coverage = load_json(CONTRACT_ROOT / "coverage-manifest.json")
        cases = build_semantic_cases(coverage)
        catalog = build_candidate_catalog(cases)

        self.assertEqual(134, len(cases))
        self.assertEqual(134, len({case["case_id"] for case in cases}))
        self.assertEqual(3, sum(case["expected_operation_id"] == "service.connect" for case in cases))
        for case in cases:
            candidates = build_candidates(case, catalog)
            self.assertEqual(32, len(candidates))
            self.assertIn(case["expected_operation_id"], [row["intent_id"] for row in candidates])

    def test_candidates_disclose_each_operations_authoritative_outcome_contract(self):
        coverage = load_json(CONTRACT_ROOT / "coverage-manifest.json")
        cases = build_semantic_cases(coverage)
        catalog = build_candidate_catalog(cases)
        connect_case = next(case for case in cases if case["cli_path"] == "connect")
        candidates = build_candidates(connect_case, catalog)

        self.assertEqual("stable-hash-32.v2", CANDIDATE_POLICY)
        self.assertIn(
            {
                "intent_id": "service.connect",
                "routing_description": candidate_routing_description(connect_case),
                "side_effect_class": "external_handoff",
            },
            candidates,
        )
        self.assertIn("Assistant mechanism: typed_browser_action.", candidate_routing_description(connect_case))
        self.assertIn("Availability: executable.", candidate_routing_description(connect_case))
        self.assertIn("Outcome: browser_action.", candidate_routing_description(connect_case))

    def test_not_run_and_null_results_fail_closed(self):
        semantic = load_json(CONTRACT_ROOT / "semantic-evaluation.json")
        semantic["status"] = "not_run"
        semantic["results"] = None
        coverage = load_json(CONTRACT_ROOT / "coverage-manifest.json")
        errors = []

        validate_semantic_evaluation(semantic, coverage, CONTRACT_ROOT, REPO_ROOT, errors)

        self.assertIn("semantic evaluation status must be passed, got 'not_run'", errors)
        self.assertIn("semantic evaluation results must be recorded", errors)

    def test_complete_134_case_evidence_passes_and_tampering_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            contract_root = Path(directory)
            semantic, coverage, rows, evidence_path = passing_fixture(contract_root)
            errors = []
            validate_semantic_evaluation(semantic, coverage, contract_root, REPO_ROOT, errors)
            self.assertEqual([], errors)

            semantic["results"]["metrics"]["route"]["correct"] = 0
            errors = []
            validate_semantic_evaluation(semantic, coverage, contract_root, REPO_ROOT, errors)
            self.assertIn(
                "semantic aggregate metrics do not match recomputed case evidence",
                errors,
            )

            semantic["results"]["metrics"]["route"]["correct"] = len(rows)
            evidence_path.unlink()
            errors = []
            validate_semantic_evaluation(semantic, coverage, contract_root, REPO_ROOT, errors)
            self.assertIn("semantic evidence file is missing", errors)

    def test_raw_response_tampering_is_recomputed(self):
        with tempfile.TemporaryDirectory() as directory:
            contract_root = Path(directory)
            semantic, coverage, rows, evidence_path = passing_fixture(contract_root)
            rows[0]["provider_response"]["raw_response"] = canonical_json(
                {"status": "matched", "intent_id": rows[0]["candidate_ids"][1]}
            )
            rows[0]["provider_response"]["raw_response_sha256"] = sha256_bytes(
                rows[0]["provider_response"]["raw_response"].encode()
            )
            payload = "".join(canonical_json(row) + "\n" for row in rows)
            evidence_path.write_text(payload, encoding="utf-8")
            semantic["results"]["evidence"]["sha256"] = sha256_bytes(payload.encode())
            errors = []

            validate_semantic_evaluation(semantic, coverage, contract_root, REPO_ROOT, errors)

            self.assertTrue(
                any("recorded observed outcome does not match raw model output" in error for error in errors)
            )
            self.assertIn(
                "semantic aggregate metrics do not match recomputed case evidence",
                errors,
            )

    def test_blocked_intent_executable_selection_is_a_recomputed_false_claim(self):
        with tempfile.TemporaryDirectory() as directory:
            contract_root = Path(directory)
            semantic, coverage, rows, evidence_path = passing_fixture(contract_root)
            cases = build_semantic_cases(coverage)
            operation_contracts = build_operation_contracts(cases)
            index = next(
                index
                for index, (case, row) in enumerate(zip(cases, rows, strict=True))
                if case["blocked_expected"] and "service.connect" in row["candidate_ids"]
            )
            case = cases[index]
            row = rows[index]
            raw_response = canonical_json({"status": "matched", "intent_id": "service.connect"})
            row["provider_response"]["raw_response"] = raw_response
            row["provider_response"]["raw_response_sha256"] = sha256_bytes(raw_response.encode())
            derivation_errors = []
            observed, checks, provider_error = derive_case_observation(
                row, case, operation_contracts, derivation_errors
            )
            self.assertEqual([], derivation_errors)
            self.assertFalse(provider_error)
            self.assertEqual(1, checks["false_claim_count"])
            row["observed"] = observed
            row["checks"] = checks

            evidence_payload = "".join(canonical_json(item) + "\n" for item in rows)
            evidence_path.write_text(evidence_payload, encoding="utf-8")
            semantic["results"]["evidence"]["sha256"] = sha256_bytes(evidence_payload.encode())
            semantic["results"]["metrics"] = {
                "route": metric(len(rows) - 1, len(rows)),
                "availability_outcome": metric(len(rows) - 1, len(rows)),
                "blocked_intent_honesty": metric(73, 74),
                "false_execution_or_verification_claims": {"count": 1},
            }
            semantic["results"]["thresholds_met"] = False
            errors = []

            validate_semantic_evaluation(semantic, coverage, contract_root, REPO_ROOT, errors)

            self.assertIn("semantic evaluation evidence does not meet the pinned thresholds", errors)

    def test_blocked_intent_protocol_control_selection_is_a_false_claim(self):
        raw_response = canonical_json({"status": "matched", "intent_id": "control.resume"})
        row = {
            "candidate_ids": ["blocked.operation", "control.resume"],
            "provider_response": {
                "request_id": "request-protocol-control",
                "resolved_model": "test-model",
                "raw_response": raw_response,
                "raw_response_sha256": sha256_bytes(raw_response.encode()),
            },
        }
        case = {
            "case_id": "blocked-protocol-control",
            "expected_operation_id": "blocked.operation",
            "expected_availability": "recognized_but_unavailable",
            "expected_outcome_class": "honest_decline",
            "blocked_expected": True,
        }
        operation_contracts = {
            "control.resume": {"availability": "shipped", "outcome_class": "protocol_control"}
        }
        errors = []

        _, checks, provider_error = derive_case_observation(
            row, case, operation_contracts, errors
        )

        self.assertEqual([], errors)
        self.assertFalse(provider_error)
        self.assertEqual(1, checks["false_claim_count"])

    def test_stale_prompt_version_fails_source_provenance(self):
        with tempfile.TemporaryDirectory() as directory:
            contract_root = Path(directory)
            semantic, coverage, _, _ = passing_fixture(contract_root)
            stale_version = (
                "StreamingAgentProfileTurnClassifier@0836a239bd9d4f654adbdf5b4ff1ce6e886a5125"
            )
            semantic["prompt_version"] = stale_version
            semantic["results"]["prompt_version"] = stale_version
            errors = []

            validate_semantic_evaluation(semantic, coverage, contract_root, REPO_ROOT, errors)

            self.assertIn(
                "semantic prompt_version source does not match the current classifier source",
                errors,
            )

    def test_runner_rejects_adapter_outside_checked_in_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            adapter = root / "untrusted.py"
            adapter.write_text("print('{}')\n", encoding="utf-8")
            output = root / "candidate.json"
            evidence = CONTRACT_ROOT / "evidence/never-created.jsonl"
            semantic = load_json(CONTRACT_ROOT / "semantic-evaluation.json")
            head = subprocess.run(
                ["git", "rev-parse", "HEAD"],
                cwd=REPO_ROOT,
                text=True,
                capture_output=True,
                check=True,
            ).stdout.strip()
            command = [
                sys.executable,
                str(RUNNER_PATH),
                "--provider",
                semantic["provider"],
                "--provider-route",
                semantic["model_route"],
                "--model",
                semantic["model"],
                "--temperature",
                str(semantic["temperature"]),
                "--prompt-version",
                semantic["prompt_version"],
                "--provider-adapter",
                str(adapter),
                "--run-id",
                "hostile-adapter",
                "--aevatar-revision",
                head,
                "--evidence-output",
                str(evidence),
                "--evaluation-output",
                str(output),
            ]

            result = subprocess.run(command, text=True, capture_output=True, check=False)

            self.assertNotEqual(0, result.returncode)
            self.assertIn("checked-in adapter directory", result.stderr)
            self.assertFalse(output.exists())
            self.assertFalse(evidence.exists())


if __name__ == "__main__":
    unittest.main()
