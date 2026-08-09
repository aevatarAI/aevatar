#!/usr/bin/env python3

import hashlib
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
                "checked_in_payload": "registry-v4.json",
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


if __name__ == "__main__":
    unittest.main()
