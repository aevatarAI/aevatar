# Unify NyxID Assistant Chat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Mainnet `/api/chat` and `/api/chat/conversations/**` the canonical NyxID Assistant facade while preserving Workflow Chat compatibility and forcing missing services through the typed `nyxid_require_service` receipt path.

**Architecture:** Mainnet owns the only public `/api/chat` mapping and delegates discriminator-free JSON/multipart requests to the existing Workflow handler. Explicit Assistant types delegate to public NyxIdChat application handlers; first text creates the existing conversation actor and starts its first turn through one typed actor command so registration and turn admission remain actor-ordered. Public resource handlers derive scope only from the authenticated principal and reuse the existing registry, history, state, lifecycle, approval, and control ports.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, protobuf actor commands/events, xUnit, FluentAssertions.

## Global Constraints

- Preserve `NyxIdChatConversationGAgent` and its committed projection as the only conversation/task authority.
- Preserve Workflow Host `/api/chat`, `/api/ws/chat`, JSON without Assistant `type`, and multipart behavior.
- Never accept `scopeId` in new public paths or bodies; derive one unambiguous authenticated scope claim.
- Do not add HTTP loopback, ID mappings, state mirrors, provider/slug special cases, or generic facade infrastructure.
- Keep secrets and bearer credentials transient; never persist or return them.
- Baseline `dotnet test aevatar.slnx --nologo` has three unrelated static-asset injection failures; completion may retain those exact failures but must add none.

---

### Task 1: Mainnet Route Ownership and Assistant Dispatch

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowCapabilityHostBuilderExtensions.cs`
- Create: `src/Aevatar.Mainnet.Host.Api/Chat/MainnetChatEndpoints.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`
- Create: `test/Aevatar.Capabilities.Tests/MainnetChatEndpointsTests.cs`

**Interfaces:**
- Consumes: `WorkflowCapabilityEndpoints.HandleChatPost`, `NyxIdChatPublicEndpoints.HandleAsync`.
- Produces: `MapMainnetChatEndpoints()` with exactly one `POST /api/chat` on Mainnet.

- [ ] Write a route-composition test proving Mainnet maps one `POST /api/chat` while standalone Workflow mapping still maps its own route.
- [ ] Run the focused test and confirm RED because Mainnet currently auto-maps Workflow `/api/chat`.
- [ ] Split Workflow endpoint composition so Mainnet can map Workflow non-chat endpoints without changing standalone behavior.
- [ ] Add the Mainnet handler that preserves multipart and discriminator-free JSON, recognizes only the seven Assistant types, and rejects unknown explicit types.
- [ ] Run the focused tests and confirm GREEN.

### Task 2: Public NyxID Assistant Application Boundary

**Files:**
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatPublicEndpoints.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatInteraction.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/protos/nyxid_chat_task.proto` only if the combined command requires a persisted typed submessage.
- Create: `test/Aevatar.AI.Tests/NyxIdChatPublicEndpointsTests.cs`

**Interfaces:**
- Consumes: existing lifecycle, interaction, control, registry, history, and current-state ports.
- Produces: public Assistant request DTOs and route handlers for text, action continuation, approval, controls, list, transcript, state, and delete.

- [ ] Write tests proving first text creates one actor and emits authoritative actor/turn context, continued text reuses it, body idempotency wins over `Idempotency-Key`, and scope comes only from claims.
- [ ] Write tests proving list/transcript/state/delete and every control type call the existing port with the exact conversation identity and emit public recovery URLs.
- [ ] Run focused tests and confirm RED because the public boundary does not exist.
- [ ] Implement the combined create-and-first-turn command by reusing the existing create/start handlers in one actor turn; do not poll registry or prime projections from query paths.
- [ ] Implement the thinnest public handlers and exact validation needed to pass the tests.
- [ ] Run focused tests and confirm GREEN, then run query/projection and test-stability guards.

### Task 3: Typed Missing-Service Policy

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.textproto`
- Modify: generated profile snapshots only through the repository's existing profile tool if required.
- Modify: relevant profile/policy tests under `test/Aevatar.Capabilities.Tests` or `test/Aevatar.AI.Tests`.

**Interfaces:**
- Consumes: existing `NyxIdRequireServiceTool.CreateResultReceipt` and `NyxIdChatBrowserActions.RequestAuthorization`.
- Produces: final NyxIdChat policy containing `nyxid_require_service` and excluding `nyxid_services` plus broad credential-management tools.

- [ ] Write a policy test that fails unless service-connect selects `nyxid_require_service` and broad tools are absent.
- [ ] Run it and confirm RED.
- [ ] Make the smallest reviewed-profile change and regenerate/validate its immutable snapshot if the existing tooling requires it.
- [ ] Run the policy, arbitrary-slug, ready-service, stale-source, malformed-result, slug-mismatch, action, postcondition, and secret-boundary tests; confirm GREEN.

### Task 4: Canonical Contract Documentation and Verification

**Files:**
- Modify: `docs/canon/chat-api.md`
- Modify: `docs/canon/nyxid-chat-api.md`

**Interfaces:**
- Produces: self-contained canonical content types, discriminators, receipts, cursors, errors, compatibility, and scoped-route deprecation guidance.

- [ ] Update both canonical documents to make `/api/chat` and `/api/chat/conversations/**` public and scoped endpoints compatibility-only.
- [ ] Run `bash tools/docs/lint.sh` and architecture guards.
- [ ] Run focused tests, relevant solution slices, full build, and full test; compare full-test failures with the three recorded baseline failures.
- [ ] Review `git diff`, commit only issue files, fetch/rebase safely if remote moved, and push `HEAD:feature/integrate` without force.
