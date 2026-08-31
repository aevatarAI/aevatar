# Workflow Starter Templates Design

**Date:** 2026-08-27

## Goal

Add ten portable workflow template YAMLs for the vNext workflow authoring experience. Each template must parse with both the production workflow parser and Studio authoring parser, use registered primitives, remain free of tenant-owned identities and credentials, and be safe to import from the workflow-activity-vnext workflow editor. Draft-run claims remain scoped to runtime paths that can be verified on the selected backend baseline.

## Template Set

| Template | Job | Runtime shape |
| --- | --- | --- |
| `invoice_review_approval` | Review invoice evidence and require a person to approve or reject the prepared decision | LLM review -> human approval -> explicit result |
| `resume_screening_review` | Compare a resume with supplied role criteria without making an autonomous hiring decision | LLM screening -> human review -> explicit result |
| `support_triage` | Classify a support request and draft a response for an operator | LLM triage -> LLM response draft -> result |
| `research_report` | Synthesize only the source material supplied with the run | LLM research -> LLM editorial review -> result |
| `approval_gated_action` | Prepare an action plan and release it only after approval | LLM plan -> human approval -> explicit result |
| `long_running_task_handoff` | Hand work to an external worker and durably continue when its callback arrives | capture request -> emit -> initialize missing-callback sentinel -> wait signal -> LLM review -> result |
| `enterprise_knowledge_assistant` | Answer from approved context supplied in the run and expose missing evidence | grounded LLM answer -> result |
| `meeting_follow_up` | Extract decisions, owners, and due dates from meeting notes | LLM extraction -> LLM quality review -> result |
| `security_alert_triage` | Assess an alert while keeping escalation under human control | LLM triage -> human approval -> explicit result |
| `scheduled_monitor` | Evaluate one scheduled observation against a supplied baseline and policy | LLM evaluation -> result |

## Product Semantics

- Template assets live under `workflow-templates/`; they are reusable import sources, not scope-owned workflow drafts and not published services.
- Instantiation creates a new scope-owned draft with a new `workflowId`. A template identifier never substitutes for `memberId`, `workflowId`, or `publishedServiceId`.
- Scheduling belongs to the workflow-scoped Schedule resource. `scheduled_monitor` evaluates one invocation and does not create or mutate its own schedule.
- External side effects are excluded from the starter defaults. Approval produces a reviewed payload; users attach a connector or published service only after configuring its authorization boundary.
- The long-running handoff intentionally suspends on `task_completed`. Its callback payload is the durable continuation input, not a synchronous request/reply result.
- Portable YAML uses only Studio-authorable root fields: `name`, `description`, `configuration`, `roles`, and `steps`. Usage guidance belongs in `description`; runtime-only `when_to_use` is not accepted by the vNext Import YAML flow.
- Human approval result branches explicitly converge on a terminal pass-through step. Approved results retain the reviewed artifact alongside the approval response; non-approved results retain rejection or timeout-safe context without falling through into the opposite branch.
- The long-running handoff preserves the original request before emitting it. It also initializes a missing-callback sentinel before `wait_signal`, so an empty callback cannot be mistaken for a completed worker result.
- Evidence-review workflows capture the original run input before drafting so a later reviewer can compare the draft against the source material instead of reviewing it without evidence.

## Portability And Safety

- Every LLM role declares `allowed_tools: []`, so imported templates cannot inherit an ambient tool catalog.
- No template embeds connector names, service identities, team/member/workflow identities, credentials, webhook secrets, or tenant-owned NyxID capability selectors.
- Inputs are plain text or JSON supplied by the caller. Prompts state the expected input contract and prohibit invented evidence.
- Human rejection uses `on_reject: skip` plus explicit `true`/`false` branches, preserving a normal terminal result instead of silently executing an action.
- Template names equal their file basenames and are stable snake_case identifiers.

## Verification

1. A focused repository test asserts that all ten assets exist, names match filenames, descriptions and usage guidance are present, references resolve, all primitives are production-registered, LLM tool scopes are explicit, and no external authorization dependency is embedded.
2. A focused Studio test sends every asset through `WorkflowEditorService` parse, normalize, serialize, and reparse, then saves and reopens it through `AppScopedWorkflowService` with the repository's in-memory workspace port. This proves the editor and application-service contracts while retaining the honest `accepted / projection_pending` acknowledgement semantics; it does not claim production read-model visibility.
3. A focused execution test runs each human-approval starter through the production parser, workflow kernel, assign module, and approval module for approve, reject, and timeout decisions. LLM completion is isolated at the provider boundary so the test remains deterministic. A separate long-running callback/module contract uses the real assign and wait-signal modules to prove exact early-signal buffering, empty-payload sentinel fallback, and one-shot buffer consumption; it does not claim full emit-to-kernel execution on this baseline.
4. Browser verification imports each YAML once through `/scopes/:scopeId/workflow-activity-vnext/workflows/new` and requires its own fresh `/scopes/:scopeId/workflow-activity-vnext/workflows/:workflowId` editor. Each pass requires zero diagnostics and a matching Canvas graph. The same workflow URL must be reloaded and resolve the same workflow ID, name, and step graph before that draft is considered durably materialized. Approval and wait-signal templates require their expected suspended state and continuation controls before browser runtime verification can be claimed.
5. On this baseline, the ten files are repository assets for explicit Import YAML authoring. Remote public-catalog visibility requires a separate template-specific bootstrap, actor authority, projection path, and host packaging; local browser validation must not add a mock/fallback catalog or register templates as ordinary run-by-name workflow sources.

Parser round-trips and a same-page `Saved at` state prove authoring compatibility, not durable materialization or execution. If the remote read model does not return the same workflow after reload, publication and draft-run verification remain blocked and must be reported as an environment limitation rather than inferred from the authoring checks.

The requested vNext base predates the runtime repair that self-delivers authored `emit` completion so the workflow kernel can advance to the next step. Full `long_running_task_handoff` execution must be verified on a runtime baseline containing that repair; this template-only change does not duplicate the backend fix or manufacture a passing harness by feeding outward events back into the publisher.
