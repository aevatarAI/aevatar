#!/usr/bin/env python3
"""Validate project reference purity for abstraction and contract projects."""

from __future__ import annotations

import argparse
import csv
from dataclasses import dataclass
from datetime import date, datetime
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


RULE_ABSTRACTIONS_CONTRACTS_PURITY = "abstractions-contracts-purity"
KNOWN_RULES = {RULE_ABSTRACTIONS_CONTRACTS_PURITY}
FORBIDDEN_ALLOWLIST_EDGES = {
    ("Aevatar.GAgents.Channel.Abstractions", "Aevatar.CQRS.Projection.Core"),
    ("Aevatar.CQRS.Projection.Core.Abstractions", "Aevatar.Foundation.Core"),
}


@dataclass(frozen=True)
class Project:
    name: str
    path: Path
    relative_path: Path
    kind: str


@dataclass(frozen=True)
class ProjectReference:
    source: Project
    target: Project


@dataclass(frozen=True)
class AllowlistEntry:
    source: str
    target: str
    rule: str
    owner_issue: str
    expires_on: date
    reason: str


@dataclass(frozen=True)
class Violation:
    source: Project
    target: Project
    rule: str
    message: str
    allowed_by: AllowlistEntry | None = None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Check .csproj ProjectReference layer constraints."
    )
    parser.add_argument("--root", type=Path, required=True, help="Repository root.")
    parser.add_argument(
        "--allowlist",
        type=Path,
        required=True,
        help="TSV allowlist path.",
    )
    parser.add_argument(
        "--mode",
        choices=("fail", "report"),
        default="fail",
        help="Fail on violations or only report them. Allowlist shape always fails.",
    )
    return parser.parse_args()


def classify_project(name: str) -> str:
    segments = set(name.split("."))
    if "Abstractions" in segments:
        return "Abstractions"
    if "Contracts" in segments:
        return "Contracts"
    if segments.intersection({"Protos", "Proto", "Protobuf"}):
        return "ProtobufCarrier"
    return "Concrete"


def strip_xml_namespace(root: ET.Element) -> None:
    for element in root.iter():
        if "}" in element.tag:
            element.tag = element.tag.rsplit("}", 1)[1]


def read_project_name(path: Path) -> str:
    tree = ET.parse(path)
    root = tree.getroot()
    strip_xml_namespace(root)
    assembly_name = root.findtext(".//AssemblyName")
    if assembly_name and assembly_name.strip():
        return assembly_name.strip()
    return path.stem


def iter_project_paths(root: Path) -> list[Path]:
    scan_roots = [root / "src", root / "agents", root / "src" / "workflow"]
    projects: set[Path] = set()
    for scan_root in scan_roots:
        if not scan_root.exists():
            continue
        for project_path in scan_root.rglob("*.csproj"):
            parts = set(project_path.parts)
            if "bin" in parts or "obj" in parts:
                continue
            projects.add(project_path.resolve())
    return sorted(projects)


def load_projects(root: Path) -> dict[Path, Project]:
    projects: dict[Path, Project] = {}
    for path in iter_project_paths(root):
        name = read_project_name(path)
        projects[path] = Project(
            name=name,
            path=path,
            relative_path=path.relative_to(root),
            kind=classify_project(name),
        )
    return projects


def iter_project_references(project_path: Path) -> list[Path]:
    tree = ET.parse(project_path)
    root = tree.getroot()
    strip_xml_namespace(root)
    references: list[Path] = []
    for item in root.findall(".//ProjectReference"):
        include = item.attrib.get("Include")
        if not include:
            continue
        include = include.replace("\\", "/")
        references.append((project_path.parent / include).resolve())
    return references


def load_project_references(projects: dict[Path, Project]) -> list[ProjectReference]:
    references: list[ProjectReference] = []
    for source_path, source_project in projects.items():
        for target_path in iter_project_references(source_path):
            target_project = projects.get(target_path)
            if target_project is None:
                continue
            references.append(ProjectReference(source=source_project, target=target_project))
    return references


def parse_owner_issue(value: str) -> bool:
    return len(value) >= 2 and value[0] == "#" and value[1:].isdigit()


def validate_allowlist(path: Path, today: date) -> tuple[dict[tuple[str, str, str], AllowlistEntry], list[str]]:
    if not path.exists():
        return {}, [f"Allowlist file is missing: {path}"]

    errors: list[str] = []
    entries: dict[tuple[str, str, str], AllowlistEntry] = {}
    with path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.reader(handle, delimiter="\t")
        for line_number, row in enumerate(reader, start=1):
            if not row or all(not value.strip() for value in row):
                continue
            if line_number == 1 and row == [
                "source_project",
                "target_project",
                "rule",
                "owner_issue",
                "expires_on",
                "reason",
            ]:
                continue
            if len(row) != 6:
                errors.append(
                    f"{path}:{line_number}: expected 6 tab-separated fields, got {len(row)}"
                )
                continue
            source, target, rule, owner_issue, expires_text, reason = [
                value.strip() for value in row
            ]
            if not all((source, target, rule, owner_issue, expires_text, reason)):
                errors.append(f"{path}:{line_number}: allowlist fields must be non-empty")
                continue
            if rule not in KNOWN_RULES:
                errors.append(f"{path}:{line_number}: unknown rule: {rule}")
                continue
            if not parse_owner_issue(owner_issue):
                errors.append(f"{path}:{line_number}: owner_issue must match #NNNN")
                continue
            try:
                expires_on = datetime.strptime(expires_text, "%Y-%m-%d").date()
            except ValueError:
                errors.append(f"{path}:{line_number}: expires_on must be YYYY-MM-DD")
                continue
            if expires_on < today:
                errors.append(f"{path}:{line_number}: allowlist entry is expired: {expires_text}")
                continue
            key = (source, target, rule)
            if key in entries:
                errors.append(f"{path}:{line_number}: duplicate allowlist entry: {source} -> {target} {rule}")
                continue
            if (source, target) in FORBIDDEN_ALLOWLIST_EDGES:
                errors.append(
                    f"{path}:{line_number}: {source} -> {target} must not be allowlisted"
                )
                continue
            entries[key] = AllowlistEntry(
                source=source,
                target=target,
                rule=rule,
                owner_issue=owner_issue,
                expires_on=expires_on,
                reason=reason,
            )
    return entries, errors


def find_violations(
    references: list[ProjectReference],
    allowlist: dict[tuple[str, str, str], AllowlistEntry],
) -> list[Violation]:
    violations: list[Violation] = []
    allowed_target_kinds = {"Abstractions", "Contracts", "ProtobufCarrier"}
    for reference in references:
        if reference.source.kind not in {"Abstractions", "Contracts"}:
            continue
        if reference.target.kind in allowed_target_kinds:
            continue
        rule = RULE_ABSTRACTIONS_CONTRACTS_PURITY
        allowlist_entry = allowlist.get((reference.source.name, reference.target.name, rule))
        violations.append(
            Violation(
                source=reference.source,
                target=reference.target,
                rule=rule,
                allowed_by=allowlist_entry,
                message=(
                    f"{reference.source.name} ({reference.source.kind}) must only reference "
                    f"Abstractions, Contracts, or protobuf carrier projects; "
                    f"target {reference.target.name} is {reference.target.kind}."
                ),
            )
        )
    return violations


def print_violation(violation: Violation, prefix: str) -> None:
    print(
        f"{prefix}: {violation.rule}: {violation.source.name} -> {violation.target.name}\n"
        f"  source: {violation.source.relative_path} [{violation.source.kind}]\n"
        f"  target: {violation.target.relative_path} [{violation.target.kind}]\n"
        f"  reason: {violation.message}\n"
        f"  fix: move the dependency behind an abstraction/contract/protobuf carrier or invert it."
    )


def main() -> int:
    args = parse_args()
    root = args.root.resolve()
    allowlist_path = args.allowlist.resolve()

    allowlist, allowlist_errors = validate_allowlist(allowlist_path, date.today())
    if allowlist_errors:
        print("Project reference layer allowlist is invalid.", file=sys.stderr)
        for error in allowlist_errors:
            print(error, file=sys.stderr)
        return 1

    projects = load_projects(root)
    references = load_project_references(projects)
    violations = find_violations(references, allowlist)
    unapproved = [violation for violation in violations if violation.allowed_by is None]
    approved = [violation for violation in violations if violation.allowed_by is not None]

    unapproved_prefix = "ERROR" if args.mode == "fail" else "WARNING report-only"
    for violation in unapproved:
        print_violation(violation, unapproved_prefix)
    for violation in approved:
        entry = violation.allowed_by
        print_violation(violation, "WARNING allowlisted")
        print(
            f"  allowlist: {entry.owner_issue} expires {entry.expires_on.isoformat()} - {entry.reason}"
        )

    if approved or unapproved:
        print(
            f"Project reference layer guard scanned {len(projects)} projects and "
            f"{len(references)} ProjectReference edges."
        )

    if unapproved and args.mode == "fail":
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
