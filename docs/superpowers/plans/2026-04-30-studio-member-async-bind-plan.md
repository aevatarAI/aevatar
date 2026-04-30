# StudioMember Async Bind Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the #516 StudioMember async bind protocol so bind admission is actor-owned and no longer depends on stale read models.

**Architecture:** Extend the existing `StudioMemberGAgent` with member-owned binding authority state and add a short-lived `StudioMemberBindingRunGAgent` for one bind request. HTTP bind returns `202 + bindingRunId`; platform binding progresses through async continuation messages rather than waiting for read-model visibility inside an actor turn.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, Google Protobuf / Grpc.Tools, existing Aevatar actor runtime, Studio projection read models.

---

## File Map

- `agents/Aevatar.GAgents.StudioMember/studio_member_messages.proto`: add binding run state/messages, update member state with binding authority sub-state, replace bound event with completed event.
- `agents/Aevatar.GAgents.StudioMember/StudioMemberGAgent.cs`: apply admission, platform-pending, completion, failure, and stale-run guards.
- `agents/Aevatar.GAgents.StudioMember/StudioMemberBindingRunGAgent.cs`: new run actor for request/admission/platform continuation state.
- `agents/Aevatar.GAgents.StudioMember/StudioMemberConventions.cs`: add run actor id builder.
- `src/Aevatar.Studio.Application/Studio/Contracts/MemberContracts.cs`: add accepted response, run status response, binding status fields.
- `src/Aevatar.Studio.Application/Studio/Abstractions/IStudioMemberCommandPort.cs`: add start-run command method.
- `src/Aevatar.Studio.Application/Studio/Abstractions/IStudioMemberQueryPort.cs`: add run query method.
- `src/Aevatar.Studio.Application/Studio/Services/StudioMemberService.cs`: change `BindAsync` to return accepted and stop reading member read model for admission.
- `src/Aevatar.Studio.Projection/CommandServices/ActorDispatchStudioMemberCommandService.cs`: dispatch `StudioMemberBindingRunRequested` to `StudioMemberBindingRunGAgent`.
- `src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto`: add binding status fields and `StudioMemberBindingRunDocument`.
- `src/Aevatar.Studio.Projection/Projectors/StudioMemberCurrentStateProjector.cs`: materialize binding authority state and last binding.
- `src/Aevatar.Studio.Projection/QueryPorts/ProjectionStudioMemberQueryPort.cs`: map binding status and run document.
- `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberEndpoints.cs`: return `202` for bind and add `GET /binding-runs/{bindingRunId}`.
- `test/Aevatar.Studio.Tests/*`: focused red/green tests for actor state, service admission, command dispatch, projection, endpoints.
- `apps/aevatar-console-web/src/pages/studio/components/bind/*`: update bind UI to use accepted/run status instead of immediate revision.

## Task 1: Lock Member Actor Binding State

**Files:**
- Modify: `test/Aevatar.Studio.Tests/StudioMemberGAgentStateTests.cs`
- Modify: `agents/Aevatar.GAgents.StudioMember/studio_member_messages.proto`
- Modify: `agents/Aevatar.GAgents.StudioMember/StudioMemberGAgent.cs`

- [ ] **Step 1: Write failing state tests**

Add tests proving `StudioMemberBindingCompletedEvent` updates `LastBinding` and `Binding`, failure updates only `Binding`, and stale completions do not overwrite newer state.

- [ ] **Step 2: Run red test**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --filter StudioMemberGAgentStateTests --nologo
```

Expected: fail because new proto messages/properties do not exist.

- [ ] **Step 3: Implement proto and actor transition**

Add the proto contracts from the design doc, generate through normal build, and update `StudioMemberGAgent.TransitionState` to apply the new events.

- [ ] **Step 4: Run green test**

Run the same filtered test. Expected: pass.

## Task 2: Add Binding Run Actor

**Files:**
- Create: `agents/Aevatar.GAgents.StudioMember/StudioMemberBindingRunGAgent.cs`
- Modify: `agents/Aevatar.GAgents.StudioMember/StudioMemberConventions.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberBindingRunGAgentStateTests.cs`

- [ ] **Step 1: Write failing run actor tests**

Cover request accepted, admission accepted/rejected, platform pending, platform success/failure, duplicate request no-op, duplicate terminal no-op.

- [ ] **Step 2: Run red test**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --filter StudioMemberBindingRunGAgentStateTests --nologo
```

Expected: fail because the run actor does not exist.

- [ ] **Step 3: Implement minimal run actor state machine**

Use only actor-owned state transitions and standard event dispatch. Do not call `IScopeBindingCommandPort.UpsertAsync`.

- [ ] **Step 4: Run green test**

Run the filtered test. Expected: pass.

## Task 3: Change Application Bind ACK

**Files:**
- Modify: `src/Aevatar.Studio.Application/Studio/Contracts/MemberContracts.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Abstractions/IStudioMemberService.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Abstractions/IStudioMemberCommandPort.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioMemberService.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberServiceBindingTests.cs`

- [ ] **Step 1: Write failing service test**

Add a test where `IStudioMemberQueryPort.GetAsync` throws if called, then assert `BindAsync` returns accepted with a binding run id.

- [ ] **Step 2: Run red test**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --filter StudioMemberServiceBindingTests --nologo
```

Expected: fail because current `BindAsync` reads the query port and returns immediate binding response.

- [ ] **Step 3: Implement accepted response**

Change `BindAsync` to normalize input, build a server-generated run id, dispatch through `IStudioMemberCommandPort.StartBindingRunAsync`, and return `StudioMemberBindingAcceptedResponse`.

- [ ] **Step 4: Run green test**

Run the filtered test. Expected: pass after updating obsolete synchronous tests to the new contract.

## Task 4: Dispatch Run Commands

**Files:**
- Modify: `src/Aevatar.Studio.Projection/CommandServices/ActorDispatchStudioMemberCommandService.cs`
- Test: `test/Aevatar.Studio.Tests/ActorDispatchStudioMemberCommandServiceTests.cs`

- [ ] **Step 1: Write failing dispatch test**

Assert `StartBindingRunAsync` ensures a `StudioMemberBindingRunGAgent` target and dispatches `StudioMemberBindingRunRequested`.

- [ ] **Step 2: Run red test**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --filter ActorDispatchStudioMemberCommandServiceTests --nologo
```

Expected: fail because the method does not exist.

- [ ] **Step 3: Implement dispatch**

Add `StartBindingRunAsync` and use `IActorDispatchPort.DispatchAsync` with `EnvelopeRouteSemantics.CreateDirect`.

- [ ] **Step 4: Run green test**

Run the filtered test. Expected: pass.

## Task 5: Project And Query Binding Run Status

**Files:**
- Modify: `src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto`
- Modify: `src/Aevatar.Studio.Projection/Projectors/StudioMemberCurrentStateProjector.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionStudioMemberQueryPort.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberCurrentStateProjectorTests.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberQueryPortTests.cs`

- [ ] **Step 1: Write failing projection/query tests**

Assert binding status fields project from `StudioMemberState.Binding` and run query returns a typed run response.

- [ ] **Step 2: Run red tests**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --filter "StudioMemberCurrentStateProjectorTests|StudioMemberQueryPortTests" --nologo
```

Expected: fail because fields and query method do not exist.

- [ ] **Step 3: Implement read model fields and mapping**

Add status/failure/run fields to current-state document and add `StudioMemberBindingRunDocument` only if projection source events are available in this slice. If run projection is too large, keep endpoint disabled until Task 6.

- [ ] **Step 4: Run green tests**

Run the filtered tests. Expected: pass.

## Task 6: Update HTTP Endpoints

**Files:**
- Modify: `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberEndpoints.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberEndpointsTests.cs`

- [ ] **Step 1: Write failing endpoint tests**

Assert `PUT /binding` returns accepted shape and `GET /binding-runs/{bindingRunId}` returns run status.

- [ ] **Step 2: Run red test**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --filter StudioMemberEndpointsTests --nologo
```

Expected: fail because bind still returns 200 and run endpoint is missing.

- [ ] **Step 3: Implement endpoint changes**

Return `Results.Accepted` for bind and add the run query route.

- [ ] **Step 4: Run green test**

Run the filtered test. Expected: pass.

## Task 7: Frontend Bind Flow

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/studio/components/bind/StudioMemberBindPanel.tsx`
- Modify: `apps/aevatar-console-web/src/pages/studio/components/bind/StudioMemberBindPanel.test.tsx`

- [ ] **Step 1: Write failing UI test**

Assert bind submit displays accepted/pending status and no longer requires immediate revision id.

- [ ] **Step 2: Run red test**

Run the existing frontend test command for `StudioMemberBindPanel.test.tsx` from the app package.

- [ ] **Step 3: Implement UI status handling**

Use `bindingRunId` and poll/query run status.

- [ ] **Step 4: Run green test**

Run the same frontend test.

## Task 8: Final Guards

- [ ] **Step 1: Run stability guard**

```bash
bash tools/ci/test_stability_guards.sh
```

- [ ] **Step 2: Run focused Studio tests**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --nologo
```

- [ ] **Step 3: Run architecture guards**

```bash
bash tools/ci/architecture_guards.sh
```

- [ ] **Step 4: Commit**

```bash
git add agents/Aevatar.GAgents.StudioMember src/Aevatar.Studio.Application src/Aevatar.Studio.Projection src/Aevatar.Studio.Hosting test/Aevatar.Studio.Tests apps/aevatar-console-web docs/superpowers
git commit -m "Refactor StudioMember binding to async actor protocol"
```

## Self-Review

- Spec coverage: actor-owned admission, run actor, honest ACK, typed status, read model projection, stale read-model admission, duplicate/stale handling, and no synchronous binding port await are covered by tasks.
- No placeholders remain for the critical backend path. The frontend test command must be resolved from the app package scripts during Task 7 because package scripts differ by workspace setup.
- Type consistency: v1 uses `StudioMemberBindingRunStatus`, `StudioMemberBindingFailure`, `StudioMemberBindingAuthorityState`, `StudioMemberBindingAcceptedResponse`, and server-generated `bindingRunId`.
