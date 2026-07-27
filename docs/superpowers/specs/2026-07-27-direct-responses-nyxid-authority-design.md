# Direct Responses NyxID Authority Propagation Design

## Status

Approach 1 approved on July 27, 2026.

## Goal

Make Aevatar's direct OpenAI-compatible ingress surfaces preserve the
authenticated caller's typed NyxID authority into `AgentToolExecutionContext`
so account-scoped tools such as managed `codex_exec` work without user-visible
credential setup.

The affected ingress surfaces are:

- `POST /v1/responses`;
- `POST /v1/messages`;
- `POST /v1/chat/completions`.

This is an Aevatar identity-context propagation fix. It does not change NyxID,
chrono-sandbox, OpenSandbox, the managed Codex credential model, rollout
eligibility, or the public `codex_exec` tool contract.

## Root Cause

The direct ingress path currently resolves:

- `ScopeId`;
- `OwnerSubject`;
- `OriginKind`.

It then builds an `AgentToolExecutionContext` without setting
`NyxIdAuthority`. `NyxIdCodexExecTool` therefore receives
`AgentToolNyxIdAuthorityContext.Empty`, and managed execution fails closed with
`managed_identity_unavailable` even though the same account succeeds through
the workflow path.

The workflow path works because it carries a typed
`WorkflowCallerNyxIdAuthority` and maps it explicitly into
`AgentToolNyxIdAuthorityContext`.

This difference is the failing boundary. The Ornn package, NyxID proxy,
chrono-sandbox, runner image, and managed credential lifecycle are downstream
of it.

## Approaches Considered

### Carry typed authority in `ResponsesCallerScope`

The Host resolver creates a typed NyxID authority from the subject it has
already authenticated. The three Application facades copy that value into the
tool context.

This is selected because it keeps identity verification at the Host boundary,
keeps Application dependent on a typed abstraction, and prevents any later
layer from reconstructing identity from unrelated IDs.

### Reconstruct authority from `ScopeId` or `OwnerSubject`

This would avoid extending `ResponsesCallerScope`, but it would make two
separate identities equal by convention. It is rejected because a request
scope is not a NyxID external subject, and future scope semantics could diverge
without a compiler-visible failure.

### Decode bearer or delegation tokens inside the tool/facade

This is rejected because token validation belongs at the Host boundary.
Application and tool providers must not parse transport credentials to recover
business identity, and delegation tokens must not become caller identity.

## Contract Changes

`ResponsesCallerScope` gains a typed `NyxIdAuthority` property of type
`AgentToolNyxIdAuthorityContext`.

The property defaults to `AgentToolNyxIdAuthorityContext.Empty` so the generic
Application contract remains usable by non-NyxID hosts. A NyxID Host resolver
must populate a complete value:

```text
Platform       = "nyxid"
Tenant         = ""
ExternalUserId = authenticated NyxID subject
```

The authenticated subject is resolved exactly as it is today:

1. use the request-prevalidated NyxID identity-assertion subject when present;
2. otherwise validate the supplied NyxID identity assertion and use its
   subject;
3. otherwise resolve the current NyxID user from the inbound bearer token.

The resolver must not:

- read identity from `ScopeId` after constructing the scope;
- interpret `X-NyxID-Delegation-Token` as caller identity;
- derive authority from route, skill, workflow, or service identifiers;
- persist or log the bearer, identity assertion, or delegation token.

## Application Data Flow

Each direct facade keeps its existing command lifecycle and changes only the
tool-context mapping:

```text
validated NyxID subject
  -> NyxIdResponsesCallerScopeResolver
  -> ResponsesCallerScope.NyxIdAuthority
  -> Responses/Messages/ChatCompletions BuildToolContext
  -> AgentToolExecutionContext.NyxIdAuthority
  -> LlmRunRequested.ToolContext
  -> NyxIdCodexExecTool
```

The three facades must copy the typed object supplied by
`ResponsesCallerScope`; they must not independently reconstruct it.

Existing request identity, caller scope, owner subject, bearer credential,
route preference, tool selection, streaming, dispatch, and observation
semantics remain unchanged.

## Failure Semantics

Host identity failures remain fail-closed and continue to return the existing
authentication errors.

If a non-NyxID resolver supplies `AgentToolNyxIdAuthorityContext.Empty`, generic
tools may continue normally. A tool that requires typed NyxID ownership, such
as managed `codex_exec`, retains its existing stable readiness failure instead
of guessing an owner.

No fallback from `NyxIdAuthority` to `ScopeId`, `OwnerSubject`, bearer-token
claims, or delegation-token claims is added.

## Test Strategy

Tests use deliberately different identity values:

```text
ScopeId        = "scope-alpha"
OwnerSubject   = "owner-alpha"
ExternalUserId = "nyx-user-alpha"
```

This prevents a passing test from hiding an identity substitution.

The RED-to-GREEN coverage must prove:

1. the NyxID Host resolver populates complete typed authority for a validated
   identity assertion;
2. the same resolver populates complete typed authority for the current-user
   bearer fallback;
3. supplying a delegation token does not replace or alter the authority
   resolved from the authenticated identity assertion or bearer token;
4. Responses copies the exact typed authority into the serialized
   `LlmRunRequested.ToolContext`;
5. Messages does the same;
6. Chat Completions does the same;
7. existing scope, bearer, route, streaming, and authentication behavior stays
   green.

Focused tests run first. Because tests change, the repository's polling
stability guard is mandatory. The affected solution slice and architecture
guards must pass before delivery.

## Rollout and Verification

After the fix is deployed on `feature/integrate`:

1. invoke the still-private Ornn `verify-codex-exec@1.0` through
   `/v1/responses`;
2. verify exactly one `codex_exec` function call;
3. require managed result `status=succeeded`, `target=managed_sandbox`,
   `exit_code=0`, and trimmed output `CODEX_EXEC_READY`;
4. verify the final assistant verdict starts with `AVAILABLE`;
5. only then change the immutable Ornn package permissions from private to
   public and verify anonymous search/read.

The fix does not widen Aevatar's feature flag or provisioning allowlist.
Eligible users receive transparent behavior; ineligible users continue to fail
closed with the existing account-scoped result.

## Done Criteria

The work is complete when:

- all three direct facades preserve the exact typed NyxID authority;
- no code derives NyxID identity from `ScopeId` or `OwnerSubject`;
- Host resolver and facade regression tests pass with distinct IDs;
- focused builds, tests, polling guard, and architecture guards pass;
- the deployed private `verify-codex-exec` run returns `AVAILABLE`;
- the same verified Ornn package is then public and anonymously readable.
