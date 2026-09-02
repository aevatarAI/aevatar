# Workflow Skill NyxID Authority Propagation Design

## Context

The public `aevatar-codex-exec-workflow-sample` skill starts a workflow through
`POST /api/workflow/skills/{guid}/invoke`. The endpoint currently extracts the
caller's bearer token and scope, then `UserSkillRunService` creates a
`WorkflowCallerCredential` containing only that bearer token.

Managed `codex_exec` deliberately requires a native, strongly typed NyxID
authority. The missing authority therefore causes the workflow to fail before
credential lookup with `managed_identity_unavailable`, even though the same
caller succeeds in reaching the managed proxy through `/api/chat`, whose
ingress uses `WorkflowCallerCredentialExtractor`.

## Goals

- Make one-shot Ornn workflow skill invocation use the same trusted NyxID
  credential extraction path as workflow chat ingress.
- Carry the complete `WorkflowCallerCredential` unchanged from the HTTP
  boundary into `WorkflowChatRunRequest`.
- Preserve the caller's bearer token for Ornn skill retrieval without exposing
  it in logs, responses, workflow variables, or generic metadata.
- Keep `memberId`, workflow identity, scope identity, and NyxID user identity
  separate; in particular, never infer a NyxID user from `scopeId`.

## Non-Goals

- Changing managed Codex credential provisioning, Vault storage, or the
  chrono-sandbox adapter.
- Changing scheduled skill provisioning or fire-time schedule credentials.
- Adding a fallback call to NyxID `/users/me` from `UserSkillRunService`.
- Treating an opaque bearer token as proof of native NyxID authority.

## Design

`WorkflowSkillsEndpoints.InvokeSkill` will resolve the trusted caller credential
with `WorkflowCallerCredentialExtractor.ExtractAsync`. The extractor owns the
existing mapping from authenticated claims and external identity binding facts
to `WorkflowCallerNyxIdAuthority`; the skills endpoint will not duplicate that
mapping.

`IUserSkillRunService.InvokeOnceAsync` will accept a
`WorkflowCallerCredential` rather than a raw access-token string. The service
will validate that the credential contains a non-empty normalized bearer token,
use that token only to fetch the selected Ornn skill, and pass the same typed
credential into `WorkflowChatRunRequest`.

The request flow becomes:

```text
NyxID-authenticated HTTP request
  -> WorkflowCallerCredentialExtractor
  -> WorkflowCallerCredential(bearer, NyxID authority, binding)
  -> UserSkillRunService
  -> WorkflowChatRunRequest
  -> workflow actor state
  -> AgentWorkflowToolSourceAdapter
  -> codex_exec
```

The endpoint continues to use `AevatarScopeAccessGuard` independently for the
observatory ownership scope. Scope attribution and NyxID authority are separate
facts and are never converted into one another.

## Error Handling

- Missing or malformed caller credentials remain an authentication failure at
  the endpoint and do not dispatch a workflow command.
- A valid bearer without resolvable native NyxID authority remains usable for
  workflows that do not require native authority. Managed `codex_exec` keeps its
  existing fail-closed behavior.
- Ornn lookup and workflow dispatch failures keep their existing status and
  response mapping.
- No error response includes the bearer token or identity assertion.

## Tests

- Endpoint coverage proves that an authenticated NyxID principal produces a
  typed credential containing the bearer token, platform, external user ID,
  capability scope, and resolved binding ID.
- Endpoint coverage proves malformed credentials are rejected before invoking
  `IUserSkillRunService`.
- Service coverage proves Ornn retrieval receives the bearer from the typed
  credential and workflow dispatch receives the complete credential, including
  its NyxID authority.
- Existing workflow skills, capability, stability, architecture, build, and
  documentation guards remain green.

## Rollout Verification

After Aevatar and chrono-sandbox deployment blockers are both resolved, invoke
the public `aevatar-codex-exec-workflow-sample` skill as an allowlisted user.
The committed observatory run must report `status=succeeded`,
`target=managed_sandbox`, output exactly `CODEX_EXEC_READY`, `exit_code=0`, and
a sanitized diagnostic ID.
