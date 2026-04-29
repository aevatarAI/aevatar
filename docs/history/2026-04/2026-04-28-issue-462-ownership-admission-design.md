---
title: Issue 462 Relay Ownership Admission Scope
status: history
owner: architecture
---

# Issue 462 Relay Ownership Admission Scope

Date: 2026-04-28

> Non-authoritative implementation note. The durable ownership rules live in [GAgent Registry Ownership](../../canon/gagent-registry-ownership.md) and the repository architecture rules.

## Context

Issue #462 tracks three adjacent ownership/admission problems:

- `StudioMemberService.BindAsync` reads the StudioMember read model before dispatching binding writes.
- NyxID relay scope fallback resolved tenant scope from projection-backed channel bot registrations when the callback JWT had no scope claim.
- The relay `ConversationGAgent` actor id encoded `scope_id` as an ownership component.

This PR intentionally implements only the two Relay items. The StudioMember binding lifecycle change is larger and is tracked separately in #516 so it can receive a dedicated design/review before implementation.

## Current PR Scope

The current branch narrows relay admission and routing:

- require a verified canonical `scope_id` from the NyxID relay callback token;
- reject missing verified scope instead of resolving tenant scope from projection/read models;
- carry the verified scope as typed `TransportExtras.validated_scope_id`;
- resolve downstream channel bot registration by `nyx_agent_api_key_id + validated_scope_id`;
- reject scope mismatches or ambiguous registration matches;
- build relay conversation actor ids from relay identity, verified scope, and canonical conversation key as an opaque partition key;
- deliver relay inbound envelopes through `IActorDispatchPort`.

## Explicitly Out Of Scope

The following StudioMember work is not implemented in this PR:

- replacing `BindAsync` read-model admission;
- adding Studio binding run state/events;
- adding projection continuation or binding-run actors;
- changing Studio API/frontend binding behavior.

Those changes were reverted from this branch and should be handled by #516. Any future implementation should first define an actor-owned admission/async binding protocol and confirm that projection remains a committed-fact materialization path, not a business orchestration surface.

## Architecture Decisions

### Verified Scope Is Required

Relay webhook handling treats the verified callback principal as the admission source. If the callback JWT does not carry a verified `scope_id`, the webhook returns `401`.

This removes the security-sensitive projection fallback. Projection lag or duplicate channel bot registration documents can no longer decide tenant routing for relay callbacks.

### Scope Is Typed Context, Not Actor Identity

`scope_id` is carried as typed `validated_scope_id` in the relay activity context. It is also part of the opaque actor-address hash so that the same Nyx API key and same platform conversation cannot share one `ConversationGAgent` across scopes.

The actor id is an opaque address derived from relay identity, verified scope, and the canonical conversation key. Ownership/admission decisions must use the verified typed scope and registration validation, not actor id parsing or suffix structure.

### Downstream Registration Must Match The Verified Scope

When `validated_scope_id` is present, downstream channel registration resolution lists registrations for the Nyx API key and requires exactly one registration whose `ScopeId` matches the verified scope. API-key-only single-result lookup is not used for verified relay traffic because the same API key can have duplicate or stale projection entries.

### Runtime And Dispatch Remain Separate

The relay endpoint may use `IActorRuntime` to lookup/create the conversation actor, but the inbound envelope is delivered via `IActorDispatchPort`. This keeps lifecycle/topology separate from message delivery.

## Residual Follow-Up

StudioMember binding remains the third #462 item. The known issue is that bind admission can still depend on StudioMember read-model freshness. That is not fixed here by design; it is tracked in #516.

The desired future direction is an actor-owned bind admission protocol with durable progress facts and explicit async completion/failure semantics. The concrete actor/event/API shape should be designed and reviewed before code changes begin.

## Validation

- `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo`
- `dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --nologo`
- `bash tools/ci/test_stability_guards.sh`
- `bash tools/ci/architecture_guards.sh`
- `dotnet build aevatar.slnx --no-restore --nologo`
- `git diff --check`

## External Contract Note

The local checkout does not contain `../NyxID`, so this branch could not verify the external NyxID callback-token contract from the sibling repository. The implementation assumes the relay callback token can provide a signed `scope_id`; if NyxID cannot guarantee that in production, the token contract must be updated before relying on this admission path.
