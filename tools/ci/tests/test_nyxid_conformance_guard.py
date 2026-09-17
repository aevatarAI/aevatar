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
