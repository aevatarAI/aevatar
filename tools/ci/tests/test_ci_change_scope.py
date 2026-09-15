#!/usr/bin/env python3
"""Exercise the checked-in path filters, routing shell, and coverage condition."""

import ast
import fnmatch
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest


REPO_ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = (REPO_ROOT / ".github/workflows/ci.yml").read_text()


def block(source, header):
    """Read an indented YAML block without requiring runner-side packages."""
    lines = source.splitlines()
    positions = [i for i, line in enumerate(lines) if line.strip() == header]
    if len(positions) != 1:
        raise AssertionError(f"Expected one {header!r} block, got {len(positions)}")
    start = positions[0]
    indent = len(lines[start]) - len(lines[start].lstrip())
    end = start + 1
    while end < len(lines):
        line = lines[end]
        if line.strip() and len(line) - len(line.lstrip()) <= indent:
            break
        end += 1
    return textwrap.dedent("\n".join(lines[start + 1:end]))


CHANGES = block(WORKFLOW, "changes:")
FILTERS = block(CHANGES, "filters: |")
RESOLVE = block(CHANGES, "- name: Resolve Change Scope")
COVERAGE_CONDITION = block(block(WORKFLOW, "coverage-quality:"), "if: |")


def path_matches(path, pattern):
    # The routing filters use positive globs only. A leading **/ also matches
    # zero directories, as in paths-filter's picomatch implementation.
    return fnmatch.fnmatchcase(path, pattern) or (
        pattern.startswith("**/") and fnmatch.fnmatchcase(path, pattern[3:])
    )


def resolve_scope(paths):
    env = dict(os.environ)
    for name, filter_name in re.findall(
        r"(\w+): \$\{\{ steps\.filter\.outputs\.(\w+)_count \}\}",
        block(RESOLVE, "env:"),
    ):
        patterns = [ast.literal_eval(line.strip()[2:])
                    for line in block(FILTERS, f"{filter_name}:").splitlines()
                    if line.strip()]
        env[name] = str(sum(any(path_matches(path, p) for p in patterns) for path in paths))
    with tempfile.TemporaryDirectory() as directory:
        output = Path(directory) / "output"
        output.touch()
        env["GITHUB_OUTPUT"] = str(output)
        subprocess.run(["bash", "-eu"], input=block(RESOLVE, "run: |"),
                       text=True, env=env, check=True, capture_output=True)
        return dict(line.split("=", 1) for line in output.read_text().splitlines())


def coverage_runs(outputs, event="pull_request", base="dev", ref="refs/pull/1/merge"):
    context = {
        "github.event_name": event, "github.base_ref": base, "github.ref": ref,
        **{f"needs.changes.outputs.{name}": value for name, value in outputs.items()},
    }
    expression = re.sub(r"(?:github|needs)\.[\w.]+",
                        lambda match: repr(context[match[0]]), COVERAGE_CONDITION)
    expression = " ".join(expression.replace("&&", "and").replace("||", "or").split())

    def evaluate(node):
        if isinstance(node, ast.Constant):
            return node.value
        if isinstance(node, ast.BoolOp):
            values = [evaluate(value) for value in node.values]
            return all(values) if isinstance(node.op, ast.And) else any(values)
        if isinstance(node, ast.Compare) and len(node.ops) == 1:
            left, right = evaluate(node.left), evaluate(node.comparators[0])
            if isinstance(node.ops[0], ast.Eq):
                return left == right
            if isinstance(node.ops[0], ast.NotEq):
                return left != right
        raise AssertionError(f"Unsupported coverage expression: {ast.dump(node)}")

    return evaluate(ast.parse(expression, mode="eval").body)


class ChangeScopeTests(unittest.TestCase):
    def test_job_outputs_are_connected_to_resolver(self):
        step_id = re.search(r"^id: (\w+)$", RESOLVE, re.MULTILINE)[1]
        outputs = block(CHANGES, "outputs:")
        for name in ("docs_only", "frontend_only"):
            self.assertIn(f"{name}: ${{{{ steps.{step_id}.outputs.{name} }}}}", outputs)
        self.assertIn("needs: changes", block(WORKFLOW, "coverage-quality:"))

    def test_pr_routing_depends_on_changed_files_on_every_base(self):
        frontend = ["apps/aevatar-console-web/src/app.tsx",
                    "apps/aevatar-console-web/.env.example",
                    "apps/aevatar-console-web/pnpm-lock.yaml"]
        cases = [
            (frontend, True, False, False),
            (["apps/aevatar-console-web/docs/home.md"], True, True, False),
            (["docs/home.md"], False, True, False),
            ([], False, False, True),
        ]
        for extra in ("src/Host.cs", "src/contracts.proto", "test/HostTests.cs",
                      ".github/workflows/ci.yml", "tools/ci/coverage_quality_guard.sh",
                      "Directory.Build.props", "Directory.Packages.props",
                      "global.json", "docs/home.md"):
            cases.append((frontend + [extra], False, False, True))
            if extra != "docs/home.md":
                cases.append(([extra], False, False, True))
        for base in ("dev", "main", "feat/2026-08-04_workflow-activity-vnext", "another-base"):
            for paths, frontend_only, docs_only, should_run in cases:
                with self.subTest(base=base, paths=paths):
                    outputs = resolve_scope(paths)
                    self.assertEqual(outputs["frontend_only"], str(frontend_only).lower())
                    self.assertEqual(outputs["docs_only"], str(docs_only).lower())
                    self.assertEqual(coverage_runs(outputs, base=base), should_run)

    def test_scheduled_manual_and_mainline_push_coverage_still_runs(self):
        for paths in ([], ["docs/home.md"], ["apps/aevatar-console-web/src/app.tsx"]):
            outputs = resolve_scope(paths)
            for event, ref in (("schedule", "refs/heads/dev"),
                               ("workflow_dispatch", "refs/heads/feature"),
                               ("push", "refs/heads/main"), ("push", "refs/heads/dev")):
                with self.subTest(paths=paths, event=event, ref=ref):
                    self.assertTrue(coverage_runs(outputs, event=event, ref=ref))


if __name__ == "__main__":
    unittest.main()
