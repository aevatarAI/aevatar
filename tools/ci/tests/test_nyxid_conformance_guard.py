#!/usr/bin/env python3

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[3]
CI_ROOT = REPO_ROOT / "tools/ci"
sys.path.insert(0, str(CI_ROOT))

from nyxid_conformance_guard import (  # noqa: E402
    refresh_aevatar_pin,
    validate_assistant_registry_digests,
    validate_aevatar_digests,
    validate_nyxid_wire_contract,
    validate_nyxid_wire_manifest,
    validate_nyxid_wire_source,
)


def run_git(root: Path, *arguments: str) -> str:
    result = subprocess.run(
        ["git", *arguments],
        cwd=root,
        text=True,
        capture_output=True,
        check=True,
    )
    return result.stdout.strip()


class NyxIdConformanceGuardTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.repo_root = Path(self.directory.name)
        run_git(self.repo_root, "init", "--quiet")
        run_git(self.repo_root, "config", "user.email", "guard-test@example.com")
        run_git(self.repo_root, "config", "user.name", "Conformance Guard Test")
        self.source = self.repo_root / "src/contract.txt"
        self.source.parent.mkdir(parents=True)
        self.source.write_text("declared tree\n", encoding="utf-8")
        run_git(self.repo_root, "add", "src/contract.txt")
        run_git(self.repo_root, "commit", "--quiet", "-m", "Add contract source")
        self.revision = run_git(self.repo_root, "rev-parse", "HEAD")
        self.digest = hashlib.sha256(b"declared tree\n").hexdigest()

    def tearDown(self):
        self.directory.cleanup()

    def sources(self):
        aggregate = hashlib.sha256(
            f"{self.digest}  src/contract.txt\n".encode()
        ).hexdigest()
        return {
            "aevatar": {
                "revision": self.revision,
                "contract_files_sha256": aggregate,
                "files": {"src/contract.txt": self.digest},
            },
            "generated_artifacts": {},
            "assistant_registry": {
                "checked_in_payload": "registry-v7.json",
                "checked_in_payload_sha256": "unused",
            },
        }

    def test_aevatar_digests_are_read_from_declared_tree_not_worktree(self):
        self.source.write_text("uncommitted drift\n", encoding="utf-8")
        errors = []

        validate_aevatar_digests(self.repo_root, self.sources()["aevatar"], errors)

        self.assertEqual([], errors)

    def test_declared_tree_digest_mismatch_fails(self):
        sources = self.sources()
        sources["aevatar"]["files"]["src/contract.txt"] = "0" * 64
        errors = []

        validate_aevatar_digests(self.repo_root, sources["aevatar"], errors)

        self.assertTrue(
            any("Aevatar declared-tree source drift" in error for error in errors)
        )

    def test_committed_head_drift_from_declared_revision_fails(self):
        self.source.write_text("committed drift\n", encoding="utf-8")
        run_git(self.repo_root, "add", "src/contract.txt")
        run_git(self.repo_root, "commit", "--quiet", "-m", "Drift contract source")
        errors = []

        validate_aevatar_digests(self.repo_root, self.sources()["aevatar"], errors)

        self.assertTrue(
            any("Aevatar HEAD source differs" in error for error in errors)
        )

    def test_declared_revision_must_be_an_ancestor_of_head(self):
        run_git(self.repo_root, "checkout", "--quiet", "--orphan", "unrelated")
        self.source.write_text("declared tree\n", encoding="utf-8")
        run_git(self.repo_root, "add", "src/contract.txt")
        run_git(self.repo_root, "commit", "--quiet", "-m", "Add unrelated source")
        errors = []

        validate_aevatar_digests(self.repo_root, self.sources()["aevatar"], errors)

        self.assertIn(
            "pinned Aevatar revision must be an ancestor of current HEAD",
            errors,
        )

    def test_missing_declared_revision_fails(self):
        sources = self.sources()
        sources["aevatar"]["revision"] = "f" * 40
        errors = []

        validate_aevatar_digests(self.repo_root, sources["aevatar"], errors)

        self.assertTrue(any("cannot read Aevatar revision" in error for error in errors))

    def test_refresh_resolves_commit_and_hashes_its_tree(self):
        sources = self.sources()
        sources["aevatar"]["revision"] = "0" * 40
        sources["aevatar"]["files"]["src/contract.txt"] = "0" * 64
        self.source.write_text("uncommitted drift\n", encoding="utf-8")
        errors = []

        refresh_aevatar_pin(self.repo_root, sources, "HEAD", errors)

        self.assertEqual([], errors)
        self.assertEqual(self.revision, sources["aevatar"]["revision"])
        self.assertEqual(
            self.digest,
            sources["aevatar"]["files"]["src/contract.txt"],
        )

    def test_nyxid_wire_contract_requires_pinned_markers(self):
        wire_source = self.repo_root / "backend/src/handlers/keys.rs"
        wire_source.parent.mkdir(parents=True)
        wire_source.write_text("pub auto_connected: bool,\n", encoding="utf-8")
        wire_contract = {
            "files": {
                "backend/src/handlers/keys.rs": hashlib.sha256(
                    wire_source.read_bytes()
                ).hexdigest(),
            },
            "required_markers": {
                "backend/src/handlers/keys.rs": ["pub auto_connected: bool,"],
            },
            "forbidden_markers": {
                "backend/src/handlers/keys.rs": ["pub credential: String,"],
            },
        }
        errors = []

        validate_nyxid_wire_contract(self.repo_root, wire_contract, errors)

        self.assertEqual([], errors)

    def test_nyxid_wire_contract_rejects_semantic_and_digest_drift(self):
        wire_source = self.repo_root / "backend/src/handlers/user_services_handler.rs"
        wire_source.parent.mkdir(parents=True)
        wire_source.write_text("pub auto_connected: bool,\n", encoding="utf-8")
        wire_contract = {
            "files": {
                "backend/src/handlers/user_services_handler.rs": "0" * 64,
            },
            "required_markers": {
                "backend/src/handlers/user_services_handler.rs": [
                    "pub forward_access_token: bool,",
                ],
            },
            "forbidden_markers": {
                "backend/src/handlers/user_services_handler.rs": [
                    "pub auto_connected: bool,",
                ],
            },
        }
        errors = []

        validate_nyxid_wire_contract(self.repo_root, wire_contract, errors)

        self.assertTrue(any("wire source drift" in error for error in errors))
        self.assertTrue(any("marker is missing" in error for error in errors))
        self.assertTrue(any("forbidden wire field" in error for error in errors))

    def test_nyxid_wire_marker_sources_must_be_digest_pinned(self):
        errors = []

        validate_nyxid_wire_contract(
            self.repo_root,
            {
                "files": {},
                "required_markers": {"backend/src/handlers/proxy.rs": ["required"]},
            },
            errors,
        )

        self.assertIn(
            "NyxID wire marker source is not digest-pinned: "
            "backend/src/handlers/proxy.rs",
            errors,
        )

    def test_nyxid_wire_source_accepts_descendant_head_with_unchanged_contract(self):
        wire_source = self.repo_root / "backend/src/handlers/keys.rs"
        wire_source.parent.mkdir(parents=True)
        wire_source.write_text("pub auto_connected: bool,\n", encoding="utf-8")
        run_git(self.repo_root, "add", "backend/src/handlers/keys.rs")
        run_git(self.repo_root, "commit", "--quiet", "-m", "Add wire source")
        reviewed_revision = run_git(self.repo_root, "rev-parse", "HEAD")
        digest = hashlib.sha256(wire_source.read_bytes()).hexdigest()
        run_git(self.repo_root, "commit", "--quiet", "--allow-empty", "-m", "Advance main")
        errors = []

        observed_revision = validate_nyxid_wire_source(
            self.repo_root,
            self.wire_sources(reviewed_revision, digest),
            errors,
        )

        self.assertEqual(run_git(self.repo_root, "rev-parse", "HEAD"), observed_revision)
        self.assertEqual([], errors)

    def test_nyxid_wire_manifest_rejects_non_main_tracking(self):
        sources = self.wire_sources("1" * 40, "2" * 64)
        sources["nyxid"]["tracked_ref"] = "release"
        errors = []

        validate_nyxid_wire_manifest(sources, errors)

        self.assertIn("NyxID wire manifest must track main", errors)

    @staticmethod
    def wire_sources(reviewed_revision, digest):
        return {
            "schema_version": 1,
            "nyxid": {
                "repository": "https://github.com/ChronoAIProject/NyxID.git",
                "tracked_ref": "main",
                "reviewed_revision": reviewed_revision,
            },
            "wire_contract": {
                "revision": "nyxid-code-execution-wire.v1",
                "files": {"backend/src/handlers/keys.rs": digest},
                "required_markers": {
                    "backend/src/handlers/keys.rs": ["pub auto_connected: bool,"],
                },
                "forbidden_markers": {},
            },
        }

    def test_transition_revisions_must_match_payload_pins(self):
        contract_root = Path(self.directory.name) / "contracts"
        registry = self.write_transition_payloads(contract_root)
        registry["transition_payloads"].pop("nyxid-assistant-actions.v4")
        errors = []

        validate_assistant_registry_digests(contract_root, registry, errors)

        self.assertIn(
            "assistant registry transition revisions do not match their payload pins",
            errors,
        )

    def test_transition_payload_revision_mismatch_fails(self):
        contract_root = Path(self.directory.name) / "contracts"
        registry = self.write_transition_payloads(contract_root)
        v4_payload = contract_root / "registry-v4.json"
        v4_payload.write_text(
            json.dumps({"revision": "nyxid-assistant-actions.wrong"}),
            encoding="utf-8",
        )
        registry["transition_payloads"]["nyxid-assistant-actions.v4"][
            "checked_in_payload_sha256"
        ] = hashlib.sha256(v4_payload.read_bytes()).hexdigest()
        errors = []

        validate_assistant_registry_digests(contract_root, registry, errors)

        self.assertIn(
            "assistant registry transition payload revision mismatch: "
            "nyxid-assistant-actions.v4",
            errors,
        )

    def test_transition_payload_digest_drift_and_missing_payload_fail(self):
        contract_root = Path(self.directory.name) / "contracts"
        registry = self.write_transition_payloads(contract_root)
        registry["transition_payloads"]["nyxid-assistant-actions.v4"][
            "checked_in_payload_sha256"
        ] = "0" * 64
        registry["transition_payloads"]["nyxid-assistant-actions.v5"][
            "checked_in_payload"
        ] = "missing-v5.json"
        errors = []

        validate_assistant_registry_digests(contract_root, registry, errors)

        self.assertIn(
            "assistant registry transition payload digest drift: "
            "nyxid-assistant-actions.v4",
            errors,
        )
        self.assertIn(
            "assistant registry transition payload is missing: "
            "nyxid-assistant-actions.v5",
            errors,
        )

    @staticmethod
    def write_transition_payloads(contract_root: Path):
        contract_root.mkdir()
        revisions = [
            "nyxid-assistant-actions.v4",
            "nyxid-assistant-actions.v5",
            "nyxid-assistant-actions.v6",
            "nyxid-assistant-actions.v7",
            "nyxid-assistant-actions.v8",
        ]
        transition_payloads = {}
        for revision in revisions:
            name = f"registry-v{revision.rsplit('v', 1)[1]}.json"
            payload = contract_root / name
            payload.write_text(json.dumps({"revision": revision}), encoding="utf-8")
            transition_payloads[revision] = {
                "checked_in_payload": name,
                "checked_in_payload_sha256": hashlib.sha256(
                    payload.read_bytes()
                ).hexdigest(),
            }
        return {
            "revision": "nyxid-assistant-actions.v7",
            "accepted_revisions": revisions,
            "checked_in_payload": "registry-v7.json",
            "checked_in_payload_sha256": transition_payloads[
                "nyxid-assistant-actions.v7"
            ]["checked_in_payload_sha256"],
            "transition_payloads": transition_payloads,
        }


if __name__ == "__main__":
    unittest.main()
