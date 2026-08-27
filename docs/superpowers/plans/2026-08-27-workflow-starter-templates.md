# Workflow Starter Templates Implementation Plan

> Execute in the isolated `feat/2026-08-27_workflow-starter-templates` worktree created from `feat/2026-08-04_workflow-activity-vnext` at `d343bf4f56dfde60717a4c154ba00f02698584ca`.

**Goal:** Deliver ten production-parseable starter workflow YAMLs, record browser authoring evidence and runtime limits, and avoid tenant-specific identities, credentials, or implicit external side effects.

**Architecture:** Keep reusable assets in `workflow-templates/`. Validate them with the runtime parser plus the Studio authoring parser/validator, primitive registry, and authorization dependency evaluator used by workflow execution. Reuse the existing workflow-activity-vnext workflow resource editor for browser parse/graph verification, and report any durable materialization or draft-run limit without adding a mock path.

**Tech Stack:** YAML, .NET 10, xUnit, FluentAssertions, React/Umi vNext workflow studio, remote Aevatar APIs.

---

### Task 1: Lock The Template Contract

**Files:**
- Create: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowStarterTemplateContractTests.cs`

1. Add the fixed ten-template manifest and structural/runtime contract assertions.
2. Run only the focused test and record the expected missing-directory/files failure.

### Task 2: Add The Ten YAML Assets

**Files:**
- Create: `workflow-templates/invoice_review_approval.yaml`
- Create: `workflow-templates/resume_screening_review.yaml`
- Create: `workflow-templates/support_triage.yaml`
- Create: `workflow-templates/research_report.yaml`
- Create: `workflow-templates/approval_gated_action.yaml`
- Create: `workflow-templates/long_running_task_handoff.yaml`
- Create: `workflow-templates/enterprise_knowledge_assistant.yaml`
- Create: `workflow-templates/meeting_follow_up.yaml`
- Create: `workflow-templates/security_alert_triage.yaml`
- Create: `workflow-templates/scheduled_monitor.yaml`

1. Implement only production-registered primitives.
2. Give all LLM roles explicit empty tool scopes.
3. Keep approval outcomes and continuation boundaries explicit.
4. Run the focused contract test until green.

### Task 3: Focused Repository Verification

**Files:**
- Create: `test/Aevatar.Integration.Tests/WorkflowStarterTemplateExecutionTests.cs`

1. Run the existing repository YAML parse test.
2. Run the new starter-template contract test.
3. Run the Studio editor plus in-memory scoped draft save/reopen contract for all ten templates.
4. Run deterministic approve, reject, and timeout execution cases for the four human-approval templates.
5. Run `bash tools/ci/test_stability_guards.sh` because test files changed.
6. Inspect the diff for identity leaks, hidden connector dependencies, unresolved step references, and unrelated files.

### Task 4: Browser Verification

1. Reuse the existing authenticated browser and existing vNext frontend tab.
2. Open `/scopes/:scopeId/workflow-activity-vnext/workflows/new`, import one YAML, and retain the isolated workflow draft it creates.
3. In the resulting `/scopes/:scopeId/workflow-activity-vnext/workflows/:workflowId` editor, apply and save each YAML; require zero diagnostics, inspect the matching Canvas graph, then reload the same URL and require the same workflow name and graph before treating the draft as durably materialized.
4. Draft-run representative safe inputs. For human approval and wait-signal workflows, verify the expected suspension and resume/signal surface rather than claiming synchronous completion.
5. Record remote environment or provider limitations without adding mocks or local backend services.

### Task 5: Deliver Through Git

1. Review and stage only the ten YAMLs, focused parser/authoring/approval-execution tests, and design/plan docs.
2. Commit with an imperative message.
3. Push `feat/2026-08-27_workflow-starter-templates`.
4. Create a PR against `feat/2026-08-04_workflow-activity-vnext` with exact focused commands and browser evidence; delegate broad validation to CI.
