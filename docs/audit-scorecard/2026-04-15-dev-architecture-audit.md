# Dev Architecture Audit — 2026-04-15

Status: active
Branch reviewed: `dev`
Head commit at review time: `067996e9`

## Scope

This note records the architecture issues that are still present on `dev` as of 2026-04-15.
It is intentionally narrow:

- only verified findings are listed
- all evidence points to the current `dev` branch
- this document does not propose the full target design yet

## Summary

The current `dev` branch still has three primary architecture breaks:

1. `agents/` keeps cross-request facts in process-local singleton stores
2. AGUI and SSE streaming still bypass the unified projection pipeline
3. Host and endpoint code still manages actor lifecycle and dispatch directly

There is also one important governance gap:

4. the existing architecture guard pipeline still does not cover `agents/` strongly enough

## Verified Findings

### 1. Process-local singleton state in `agents/`

These implementations still hold durable business facts in process memory instead of actor-owned or distributed state:

- `agents/Aevatar.GAgents.NyxidChat/NyxIdChatActorStore.cs`
  - `ConcurrentDictionary<string, List<ActorEntry>> _store`
- `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`
  - `services.TryAddSingleton<NyxIdChatActorStore>()`
- `agents/Aevatar.GAgents.StreamingProxy/StreamingProxyActorStore.cs`
  - `ConcurrentDictionary<string, List<RoomEntry>> _store`
  - `ConcurrentDictionary<string, List<ParticipantEntry>> _participants`
- `agents/Aevatar.GAgents.StreamingProxy/ServiceCollectionExtensions.cs`
  - `services.TryAddSingleton<StreamingProxyActorStore>()`

Why this matters:

- restart loses facts
- multi-node behavior is undefined
- facts live in middleware state instead of actor-owned or distributed state

### 2. Streaming paths still bypass projection orchestration

The repo already has projection session infrastructure:

- `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Streaming/IProjectionSessionEventHub.cs`
- `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionSessionEventProjectorBase.cs`
- `src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/WorkflowExecutionRunEventProjector.cs`

But several active streaming endpoints still subscribe directly to raw `EventEnvelope` streams and map frames inline:

- `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.cs`
  - chat stream path
  - tool approval continuation path
  - relay response path
- `agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs`
  - room chat stream path
  - room message stream path
- `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeGAgentEndpoints.cs`
  - draft run stream path
- `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs`
  - static GAgent chat stream path
  - scripting service chat stream path

Common pattern observed:

- `SubscribeAsync<EventEnvelope>(...)`
- inline `MapAndWriteEventAsync(...)` or `TryMapEnvelopeToAguiEvent(...)`
- `TaskCompletionSource(...)` for terminal frame waiting
- endpoint-owned timeout handling

Why this matters:

- AGUI is still on a second runtime path
- endpoint code is doing projection work
- the same event-to-frame mapping logic is duplicated across entry points

### 3. Host and endpoints still manage actor lifecycle and dispatch directly

These paths still create actors, resolve actors, and dispatch envelopes directly from endpoint code:

- `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs`
  - `actorRuntime.CreateAsync(...)`
  - `actor.HandleEventAsync(...)`
- `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeGAgentEndpoints.cs`
  - `actorRuntime.CreateAsync(...)`
  - `actor.HandleEventAsync(...)`
- `agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs`
  - room creation and message dispatch
- `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.cs`
  - chat request dispatch
  - approval decision dispatch
  - relay dispatch

Why this matters:

- host code is doing application orchestration
- message dispatch semantics are not isolated behind application ports
- lifecycle and streaming concerns are interleaved in the same endpoint methods

### 4. `StreamingProxyGAgent` still keeps a shadow state machine

`agents/Aevatar.GAgents.StreamingProxy/StreamingProxyGAgent.cs` still maintains:

- `private StreamingProxyGAgentState _proxyState = new();`

And advances it in:

- `TransitionState(...)`
- `ApplyProxyEvent(...)`

Why this matters:

- it creates a second state surface beside the actor's main committed state path
- reactivation and replay semantics are harder to reason about
- it weakens the "single authoritative owner" rule

### 5. Architecture guards still have a coverage blind spot

`bash tools/ci/architecture_guards.sh` does not currently provide strong coverage for the issues above.

Observed during review:

- the script passes many core checks
- on current `dev`, it ultimately fails in `playground_asset_drift_guard.sh`
- the main architecture checks are still centered on `src/`, `test/`, and selected workflow/scripting paths
- `agents/` remains under-guarded relative to the architectural risk it carries

## Prioritized Next Steps

### P0

1. move `NyxIdChatActorStore` and `StreamingProxyActorStore` facts into actor-owned or distributed state
2. stop adding new raw `SubscribeAsync<EventEnvelope>` streaming endpoints
3. add stronger architecture guards for `agents/`

### P1

1. move AGUI event mapping into projection session projectors
2. replace endpoint-local `TaskCompletionSource` terminal waiting with projection-session based observation
3. remove direct host lifecycle orchestration from streaming endpoints

### P2

1. eliminate `StreamingProxyGAgent` shadow state
2. split oversized endpoint files by capability boundary

## Notes

- I checked for sibling dependency repos before making dependency-level claims.
- `../NyxID` and `../chrono-storage` were not present in the local parent directory during this review.
- Because those repos were absent, this document only claims what can be verified inside this repository.

## Evidence Commands

Commands used during this review included:

```bash
git log --oneline --decorate -1 HEAD
bash tools/ci/architecture_guards.sh
rg -n "NyxIdChatActorStore|StreamingProxyActorStore|SubscribeAsync<EventEnvelope>|HandleEventAsync\\(envelope|TaskCompletionSource<|TaskCompletionSource\\(|TryMapEnvelopeToAguiEvent|MapAndWriteEventAsync|IProjectionSessionEventHub|ProjectionSessionEventProjectorBase|actorRuntime\\.CreateAsync\\(|actorRuntime\\.GetAsync\\(|_proxyState|ConcurrentDictionary<string, List|Dictionary<string, int> _denialCounts" agents src
wc -l src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeGAgentEndpoints.cs
ls ../NyxID
ls ../chrono-storage
```
