#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
SCAN_ROOT="${AEVATAR_NYXID_CHAT_GUARD_ROOT:-${REPO_ROOT}}"

nyxid_dir="${SCAN_ROOT}/agents/Aevatar.GAgents.NyxidChat"
task_proto="${nyxid_dir}/protos/nyxid_chat_task.proto"
readmodel_proto="${SCAN_ROOT}/src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto"

required_files=(
  "${task_proto}"
  "${readmodel_proto}"
  "${nyxid_dir}/NyxIdChatConversationGAgent.cs"
  "${nyxid_dir}/NyxIdChatTaskLifecycle.cs"
  "${nyxid_dir}/NyxIdChatTaskTransitionPolicy.cs"
  "${nyxid_dir}/NyxIdAssistantActionRegistry.cs"
)

for required_file in "${required_files[@]}"; do
  if [[ ! -f "${required_file}" ]]; then
    echo "Missing NyxIdChat semantics guard anchor: ${required_file#"${SCAN_ROOT}/"}" >&2
    exit 1
  fi
done

python3 - "${SCAN_ROOT}" <<'PY'
from __future__ import annotations

import pathlib
import re
import sys

root = pathlib.Path(sys.argv[1])
nyxid = root / "agents" / "Aevatar.GAgents.NyxidChat"
task_proto = nyxid / "protos" / "nyxid_chat_task.proto"
readmodel_proto = (
    root
    / "src"
    / "Aevatar.Studio.Projection"
    / "ReadModels"
    / "studio_projection_readmodels.proto"
)


def relative(path: pathlib.Path) -> str:
    return path.relative_to(root).as_posix()


def strip_comments(text: str) -> str:
    """Remove C-style comments while preserving quoted strings and line numbers."""
    output: list[str] = []
    index = 0
    state = "code"
    quote = ""
    while index < len(text):
        current = text[index]
        following = text[index + 1] if index + 1 < len(text) else ""

        if state == "code":
            if current == "/" and following == "/":
                output.extend((" ", " "))
                index += 2
                state = "line_comment"
                continue
            if current == "/" and following == "*":
                output.extend((" ", " "))
                index += 2
                state = "block_comment"
                continue
            if current in {'"', "'"}:
                quote = current
                state = "string"
            output.append(current)
            index += 1
            continue

        if state == "string":
            output.append(current)
            if current == "\\" and index + 1 < len(text):
                output.append(text[index + 1])
                index += 2
                continue
            if current == quote:
                state = "code"
            index += 1
            continue

        if state == "line_comment":
            if current == "\n":
                output.append(current)
                state = "code"
            else:
                output.append(" ")
            index += 1
            continue

        if current == "*" and following == "/":
            output.extend((" ", " "))
            index += 2
            state = "code"
            continue
        output.append("\n" if current == "\n" else " ")
        index += 1

    return "".join(output)


def line_number(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


violations: list[tuple[str, str, int, str]] = []


def add_files(
    files: set[pathlib.Path],
    base: pathlib.Path,
    suffixes: set[str],
    *,
    name_contains: str | None = None,
) -> None:
    if not base.exists():
        return
    for path in base.rglob("*"):
        if not path.is_file() or path.suffix not in suffixes:
            continue
        if any(part in {"bin", "obj", "node_modules"} for part in path.parts):
            continue
        if path.name.endswith(".g.cs"):
            continue
        if name_contains is not None and name_contains not in path.name.lower():
            continue
        files.add(path)


# NyxID Chat owns domain-neutral execution mechanics. Concrete business
# evidence, policy, and fixed workflow routes belong to loaded skills,
# workflows, or provider-owned typed extensions and must not leak back into
# the shared actor, projection, provider, prompt, or frontend contracts.
owned_business_semantics_files: set[pathlib.Path] = set()
add_files(owned_business_semantics_files, nyxid, {".cs", ".proto"})
for prompt_name in ("system-prompt.md", "system-skill-overlay-default.md"):
    prompt_path = nyxid / "Skills" / prompt_name
    if prompt_path.exists():
        owned_business_semantics_files.add(prompt_path)
for studio_boundary in (
    "Aevatar.Studio.Projection",
    "Aevatar.Studio.Application.Abstractions",
    "Aevatar.Studio.Infrastructure",
):
    add_files(
        owned_business_semantics_files,
        root / "src" / studio_boundary,
        {".cs", ".proto"},
        name_contains="nyxidchat",
    )
add_files(
    owned_business_semantics_files,
    root / "apps" / "aevatar-console-web" / "src" / "pages" / "chat",
    {".ts", ".tsx"},
)

# Ambiguous external names are forbidden only in Aevatar-owned production
# defaults. Tests must remain able to prove that arbitrary user content and
# provider operation names pass through generic NyxID Chat contracts unchanged.
def is_test_fixture(path: pathlib.Path) -> bool:
    path_from_root = path.relative_to(root)
    return (
        path_from_root.parts[0] == "test"
        or ".test." in path.name.lower()
        or ".spec." in path.name.lower()
    )


platform_owned_business_identity_files = {
    path for path in owned_business_semantics_files if not is_test_fixture(path)
}
mainnet_config_root = root / "src" / "Aevatar.Mainnet.Host.Api"
if mainnet_config_root.exists():
    platform_owned_business_identity_files.update(
        mainnet_config_root.glob("appsettings*.json")
    )

# Broad shared directories are checked only for unmistakable types, tool names,
# and fixed routes from the removed implementation. Common terms such as
# roleTitle or costCenter are checked only on NyxID Chat-owned surfaces.
specific_business_semantics_files = set(owned_business_semantics_files)
for production_root, suffixes in (
    (root / "agents", {".cs", ".proto"}),
    (root / "src", {".cs", ".proto"}),
    (root / "apps" / "aevatar-console-web" / "src", {".ts", ".tsx"}),
    (root / "test", {".cs", ".json", ".md", ".proto", ".yaml", ".yml"}),
    (root / "docs", {".html", ".md"}),
    (root / "apps" / "aevatar-console-web" / "docs", {".html", ".md"}),
    (root / "workflows", {".json", ".md", ".yaml", ".yml"}),
    (root / "demos", {".cs", ".json", ".md", ".proto", ".ts", ".tsx", ".yaml", ".yml"}),
    (root / "delivery-workflows", {".yaml", ".yml"}),
    (root / "workflow-delivery-packages", {".yaml", ".yml"}),
):
    add_files(specific_business_semantics_files, production_root, suffixes)
if mainnet_config_root.exists():
    specific_business_semantics_files.update(mainnet_config_root.glob("appsettings*.json"))
locale_root = root / "apps" / "aevatar-console-web" / "src" / "locales"
if locale_root.exists():
    specific_business_semantics_files.update(locale_root.glob("projectMessages.*.ts"))

# Product workflow packages are deployment-owned extension inputs. The source
# tree must not ship any package under either the legacy or current package
# directory, regardless of how its business vocabulary is spelled.
for package_root_name in ("delivery-workflows", "workflow-delivery-packages"):
    package_root = root / package_root_name
    if not package_root.exists():
        continue
    for path in sorted(package_root.rglob("*")):
        if path.is_file() and path.suffix in {".yaml", ".yml"}:
            violations.append(
                (
                    "bundled workflow delivery packages",
                    relative(path),
                    1,
                    path.name,
                )
            )

# Firecrawl output is a local design aid, not a production source artifact.
# Keeping the directory forbidden also covers binary screenshots that cannot
# be inspected by the lexical scanner below.
studio_design_cache = (
    root
    / "src"
    / "workflow"
    / "Aevatar.Workflow.Infrastructure"
    / "CapabilityApi"
    / "StudioAssistant"
    / ".firecrawl"
)
if studio_design_cache.exists():
    for path in sorted(studio_design_cache.rglob("*")):
        if path.is_file():
            violations.append(
                (
                    "production-source design cache artifacts",
                    relative(path),
                    1,
                    path.name,
                )
            )

specific_business_semantics_patterns = (
    re.compile(
        r"\b(?:NyxIdChat|Chat)(?:MoneyValue|InvoiceEvidence|"
        r"InvoiceDuplicateEvidence|ReimbursementEvidence|"
        r"CandidateRubricCriterion|CandidateCriterionScore|"
        r"CandidateScreeningEvidence|TaskDomain(?:State)?|"
        r"ReimbursementArtifact|CandidateTrackerArtifact|"
        r"Expense(?:Claim|Report)(?:Evidence|Artifact|State)|"
        r"(?:Applicant|Candidate)(?:Screening|Assessment|Scoring|Tracker)?"
        r"(?:Evidence|Artifact|State)|ResumeScreening(?:Evidence|Artifact|State)|"
        r"VerifiedArtifact(?:State)?)(?:Document|Snapshot)?\b"
    ),
    re.compile(
        r"\b(?:NyxIdChatDomainEvidenceContract|DomainEvidenceAgentToolSource|"
        r"ReimbursementEvidenceTool|CandidateScreeningEvidenceTool|"
        r"ExpenseClaimEvidenceTool|ApplicantScreeningEvidenceTool|"
        r"CandidateAssessmentEvidenceTool|ResumeScreeningEvidenceTool|"
        r"NyxIdChatDomainContinuationInput|DomainEvidenceBand|"
        r"VerifiedArtifactBand)\b"
    ),
    re.compile(
        r"\b(?:BudgetVariance|EmployeeAttendance|HROnboarding|"
        r"NewHireOnboarding)(?:Report|Fixture|Monitor|State|Artifact)?\b"
    ),
    re.compile(
        r"\b(?:(?:reimbursement|expense_claim|applicant_screening|"
        r"candidate_(?:screening|assessment|scoring)|resume_screening)"
        r"_evidence_commit)\b",
        re.IGNORECASE,
    ),
    re.compile(
        r"\b(?:reimbursement|typed[ -]+reimbursement[ -]+evidence|"
        r"expense[ -]+claim|applicant[ -]+screening|resume[ -]+screening|"
        r"candidate[ -]+screening|candidate[ -]+assessment|"
        r"candidate[ -]+scoring|candidate[ -]+tracker|"
        r"employee[ -]+attendance|(?:hr|new[ -]+hire)[ -]+onboarding|"
        r"budget[ -]+variance|"
        r"exact[ -]+duplicate[ -]+relationships?|"
        r"user[ -]+authored[ -]+rubric)\b",
        re.IGNORECASE,
    ),
    re.compile(
        r"(?<![A-Za-z0-9])(?:FIN-0[12]|fin[-_]invoice[-_]precheck[-_]approval|"
        r"fin[-_]budget[-_]variance[-_]monitor|"
        r"hr[-_]onboarding[-_]email[-_]approval|"
        r"hr[-_]monthly[-_]attendance[-_]approval|"
        r"hr[-_]attendance[-_]fill[-_]reminder|"
        r"(?:expense|reimbursement)[-_](?:claim|approval|review|submit)|"
        r"(?:applicant|candidate|resume)[-_](?:screening|assessment|scoring|tracker)|"
        r"(?:employee|staff)[-_]attendance|"
        r"(?:hr|new[-_]hire)[-_]onboarding)(?![A-Za-z0-9])",
        re.IGNORECASE,
    ),
    re.compile(
        r"(?<![A-Za-z0-9])(?:"
        r"invoice[-_]ocr[-_]policy[-_]review|"
        r"synthetic[-_]invoice[-_]review|"
        r"invoice[-_](?:match|file[-_]extract|"
        r"pdf[-_](?:extraction[-_])?workflow))"
        r"(?![A-Za-z0-9])",
        re.IGNORECASE,
    ),
    re.compile(
        r"(?<![A-Za-z0-9])(?:"
        r"invoice[ -]+(?:classifier|pdf[ -]+(?:workflow|intake[ -]+flow))|"
        r"finance[ -]+ops|run[-_]finance[-_][A-Za-z0-9][A-Za-z0-9_-]*)"
        r"(?![A-Za-z0-9])",
        re.IGNORECASE,
    ),
)

platform_owned_business_identity_patterns = (
    re.compile(
        r"(?<![A-Za-z0-9])(?:candidate[-_ ]+score|screening[-_ ]+threshold)"
        r"(?![A-Za-z0-9])",
        re.IGNORECASE,
    ),
    re.compile(
        r"(?<![A-Za-z0-9])(?:invoice[-_](?:approval|review)|"
        r"submit[-_]invoice|invoice\.submit)"
        r"(?![A-Za-z0-9])",
        re.IGNORECASE,
    ),
)

owned_business_field_patterns = (
    re.compile(
        r"\b(?:source_invoices|sourceInvoices|retained_source_ordinals|"
        r"retainedSourceOrdinals|duplicate_invoices|duplicateInvoices|"
        r"expense_category|expenseCategory|cost_center|costCenter|"
        r"reimbursement_currency_instruction|reimbursementCurrencyInstruction|"
        r"candidate_screening|candidateScreening|candidate_tracker|"
        r"candidateTracker|candidate_name|candidateName|role_title|roleTitle|"
        r"applicant_name|applicantName|candidate_score|candidateScore|"
        r"screening_score|screeningScore|assessment_score|assessmentScore|"
        r"rubric|total_score|totalScore|tracker_table_id|trackerTableId)\b"
    ),
)


def scan_business_patterns(
    path: pathlib.Path,
    text: str,
    patterns: tuple[re.Pattern[str], ...],
    *,
    line_offset: int = 0,
) -> None:
    for pattern in patterns:
        for match in pattern.finditer(text):
            violations.append(
                (
                    "business-specific NyxIdChat semantics",
                    relative(path),
                    line_offset + line_number(text, match.start()),
                    match.group(0),
                )
            )


for path in sorted(specific_business_semantics_files):
    text = path.read_text(encoding="utf-8")
    scan_business_patterns(path, text, specific_business_semantics_patterns)
    if path in owned_business_semantics_files:
        scan_business_patterns(path, text, owned_business_field_patterns)
    if path in platform_owned_business_identity_files:
        scan_business_patterns(path, text, platform_owned_business_identity_patterns)

# Actor-owned facts must not migrate into service fields. The declaration must
# be a mutable collection field and its name must carry operation/action/
# cancellation semantics. Method-local temporary collections never match the
# required member visibility prefix.
collection_field = re.compile(
    r"(?im)^[ \t]*(?:private|protected|internal|public)[ \t]+"
    r"(?:(?:static|readonly|volatile)[ \t]+)*"
    r"(?:(?:global::)?System\.Collections\.(?:Concurrent\.)?)?"
    r"(?:ConcurrentDictionary|Dictionary|HashSet|Queue)[ \t]*<[^;\n]+>"
    r"[ \t]+(?P<name>_?[A-Za-z0-9_]*(?:operation|action|cancellation)[A-Za-z0-9_]*)"
    r"[ \t]*(?:=|;)",
)
for path in sorted(nyxid.rglob("*.cs")):
    if any(part in {"bin", "obj"} for part in path.parts) or path.name.endswith(".g.cs"):
        continue
    if path.name.endswith("GAgent.cs"):
        continue
    text = strip_comments(path.read_text(encoding="utf-8"))
    for match in collection_field.finditer(text):
        violations.append(
            (
                "service-level operation/action/cancellation collections",
                relative(path),
                line_number(text, match.start()),
                match.group("name"),
            )
        )

# Controller/task/control decisions must consume typed receipts/signals. JSON
# schema parsing remains legal in the external action registry and adapters.
decision_files = (
    "NyxIdChatConversationGAgent.cs",
    "NyxIdChatTurnGAgent.cs",
    "NyxIdChatTaskLifecycle.cs",
    "NyxIdChatTaskTransitionPolicy.cs",
    "NyxIdChatControlCommands.cs",
    "NyxIdChatBrowserActions.cs",
)
json_error_inference = re.compile(
    r"(?i)(?:TryGetProperty|GetProperty)[ \t\r\n]*\([ \t\r\n]*\"error\""
    r"|\[[ \t\r\n]*\"error\"[ \t\r\n]*\]"
)
for file_name in decision_files:
    path = nyxid / file_name
    if not path.exists():
        continue
    text = strip_comments(path.read_text(encoding="utf-8"))
    for match in json_error_inference.finditer(text):
        violations.append(
            (
                'generic JSON "error" inference',
                relative(path),
                line_number(text, match.start()),
                match.group(0).replace("\n", " ").strip(),
            )
        )

# Aevatar never asks the browser to submit an OAuth/device user code. Only the
# production action registry and protobuf action contract are scanned, so
# explicit negative tests and design documentation remain free to name it.
forbidden_action = re.compile(r"(?i)device\.approve\.user_code|device_approve_user_code")
for path in (nyxid / "NyxIdAssistantActionRegistry.cs", task_proto):
    text = strip_comments(path.read_text(encoding="utf-8"))
    for match in forbidden_action.finditer(text):
        violations.append(
            (
                "device.approve.user_code",
                relative(path),
                line_number(text, match.start()),
                match.group(0),
            )
        )

forbidden_secret_fields = {
    "access_token",
    "refresh_token",
    "bearer_token",
    "oauth_token",
    "authorization",
    "authorization_header",
    "authorization_headers",
    "cookie",
    "cookies",
    "cookie_header",
    "cookie_headers",
    "client_secret",
    "user_code",
    "device_code",
    "password",
    "passphrase",
    "secret",
    "secret_value",
    "token",
    "token_value",
    "raw_body",
    "raw_upstream_body",
    "credential",
    "credential_value",
    "credentials",
    "uri_userinfo",
}
proto_field = re.compile(
    r"(?m)^[ \t]*(?:optional[ \t]+|repeated[ \t]+)?"
    r"(?:map[ \t]*<[^>]+>|[.A-Za-z_][.A-Za-z0-9_]*)"
    r"[ \t]+(?P<name>[a-z][a-z0-9_]*)[ \t]*=[ \t]*\d+[ \t]*;"
)


def scan_secret_fields(path: pathlib.Path, text: str, label: str) -> None:
    for match in proto_field.finditer(text):
        name = match.group("name")
        if name in forbidden_secret_fields:
            violations.append(
                (
                    label,
                    relative(path),
                    line_number(text, match.start()),
                    name,
                )
            )


task_text = strip_comments(task_proto.read_text(encoding="utf-8"))
scan_secret_fields(task_proto, task_text, "secret-bearing protobuf fields")

# studio_projection_readmodels.proto contains other bounded adapters. Only
# NyxIdChat conversation document messages belong to this semantic contract.
readmodel_text = strip_comments(readmodel_proto.read_text(encoding="utf-8"))
message_start = re.compile(r"\bmessage[ \t]+NyxIdChatConversation[A-Za-z0-9_]*[ \t]*\{")
for message in message_start.finditer(readmodel_text):
    depth = 0
    end = None
    for index in range(message.end() - 1, len(readmodel_text)):
        if readmodel_text[index] == "{":
            depth += 1
        elif readmodel_text[index] == "}":
            depth -= 1
            if depth == 0:
                end = index + 1
                break
    if end is None:
        violations.append(
            (
                "secret-bearing read-model fields",
                relative(readmodel_proto),
                line_number(readmodel_text, message.start()),
                "unterminated NyxIdChatConversation message",
            )
        )
        continue
    block = readmodel_text[message.start() : end]
    for field in proto_field.finditer(block):
        name = field.group("name")
        if name in forbidden_secret_fields:
            violations.append(
                (
                    "secret-bearing read-model fields",
                    relative(readmodel_proto),
                    line_number(readmodel_text, message.start() + field.start()),
                    name,
                )
            )

if violations:
    for category, path, line, detail in violations:
        print(f"{path}:{line}: {category}: {detail}")
    print(
        "NyxIdChat durable/control semantics must remain actor-owned, typed, "
        "device-code-free, and secret-free."
    )
    raise SystemExit(1)
PY

echo "NyxIdChat semantics guard passed."
