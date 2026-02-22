# ADR: Remove IAgent from IActor Interface

**Status**: Accepted  
**Date**: 2026-02-14  
**Scope**: `IActor`, `LocalActor`, `OrleansClientActor`, `GAgentGrain`, all consumers

---

## Context

The `IActor` interface is the universal handle through which application code
(Endpoints, Services, WorkflowGAgent, etc.) interacts with agents.  Two runtime
implementations exist:

| Runtime | Implementation | Where IAgent lives |
|---------|----------------|--------------------|
| Local   | `LocalActor`   | Same process, same memory |
| Orleans | `OrleansClientActor` → `GAgentGrain` | Remote Silo process |

Previously `IActor` exposed `IAgent Agent { get; }`.  In the old codebase
(`aevatar-agent-framework`), both `OrleansGAgentActor` and `SiloGAgentActor`
implemented this by throwing `NotSupportedException`:

```csharp
// Old code — client side
public IGAgent GetAgent()
{
    throw new NotSupportedException(
        "Agent instance is not available on client side.");
}

// Old code — silo side (same story!)
public IGAgent GetAgent()
{
    throw new NotSupportedException(
        "Agent instance is not available in Orleans runtime.");
}
```

This is a **leaky abstraction**: the interface promises a capability that half
of its implementations cannot deliver.  Callers discover the violation only at
runtime (`NotSupportedException`), not at compile time.

---

## Decision

### 1. Remove `IAgent` from `IActor`

`IActor` becomes a pure **RPC-safe** contract.  Every method on it works
identically whether the agent is in-process or remote:

```csharp
public interface IActor
{
    string Id { get; }
    Task ActivateAsync(CancellationToken ct = default);
    Task DeactivateAsync(CancellationToken ct = default);
    Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default);
    Task<string?> GetParentIdAsync();
    Task<IReadOnlyList<string>> GetChildrenIdsAsync();
    Task<string> GetDescriptionAsync();
    Task<string> GetAgentTypeNameAsync();
    Task ConfigureAsync(string configJson, CancellationToken ct = default);
}
```

### 2. `LocalActor.Agent` stays public

`LocalActor` is a concrete class.  It exposes `public IAgent Agent { get; }`
as a **conscious escape hatch** for code that knows it is running locally:

```csharp
public sealed class LocalActor : IActor
{
    public IAgent Agent { get; }   // Not on the interface
    // ...
}
```

Accessing it requires a cast: `((LocalActor)actor).Agent`.  The cast itself is
the signal that says *"I know this only works in Local mode"*.

### 3. RPC-safe alternatives added to `IActor`

For metadata and configuration that previously required `actor.Agent`:

| Old pattern (broken on Orleans) | New pattern (works everywhere) |
|---------------------------------|-------------------------------|
| `actor.Agent.GetType().Name` | `await actor.GetAgentTypeNameAsync()` |
| `actor.Agent.GetDescriptionAsync()` | `await actor.GetDescriptionAsync()` |
| `((GAgentBase<S,C>)actor.Agent).ConfigureAsync(cfg)` | `await actor.ConfigureAsync(json)` |
| Direct state mutation on Agent | Send event via `actor.HandleEventAsync()` |

### 4. Workflow init via event instead of direct state mutation

`WorkflowGAgent` initialization used to directly set `State.WorkflowYaml`.
This was replaced with `SetWorkflowEvent` sent through `HandleEventAsync`,
making it work identically across runtimes.

---

## Alternatives Considered

### A. `ILocalActor : IActor` sub-interface

```csharp
public interface ILocalActor : IActor { IAgent Agent { get; } }
```

Rejected: adds interface proliferation and tempts consumers to declare
variables as `ILocalActor`, silently losing runtime portability.

### B. `IAgent? Agent { get; }` on `IActor` (nullable)

Rejected: null-check discipline is fragile.  Converts a compile-time
guarantee into a runtime hope.  Orleans implementation returns null,
callers forget to check, NPE at runtime.

### C. Keep `GetAgent()` that throws `NotSupportedException`

This is what the old codebase did.  Rejected: it violates the
Liskov Substitution Principle.  An interface should not promise what
its implementations cannot deliver.

---

## Consequences

### Positive

- **Compile-time safety**: Code that depends on `IActor` cannot accidentally
  access a property that doesn't exist on remote actors.
- **Runtime homogeneity**: The same application code (Endpoints, Cognitive
  agents) runs unchanged on Local and Orleans.
- **Honest interface**: `IActor` only contains methods all implementations
  can fulfill — no `NotSupportedException` traps.

### Negative

- **Cast required for local white-box access**: Tests and demos that need
  the raw `IAgent` must cast to `LocalActor`.  This is acceptable because
  such code is inherently runtime-specific.
- **Reflection-based `ConfigureAsync`**: Both `LocalActor` and `GAgentGrain`
  walk the type hierarchy to find `GAgentBase<TState, TConfig>.ConfigureAsync`.
  This is a known trade-off; a typed config dispatch can be added later.

---

## Affected Files

| Area | Files |
|------|-------|
| **Interface** | `IActor.cs` |
| **Local Runtime** | `LocalActor.cs` |
| **Orleans Runtime** | `OrleansClientActor.cs`, `OrleansActorRuntime.cs`, `GAgentGrain.cs`, `IGAgentGrain.cs` |
| **Orleans Config** | `OrleansClientExtensions.cs`, `MassTransitKafkaExtensions.cs`, `KafkaAgentEventSender.cs` |
| **API / Gateway** | `ChatEndpoints.cs` (Api), `ChatEndpoints.cs` (Gateway), `Program.cs` (Api) |
| **Cognitive** | `WorkflowGAgent.cs`, `cognitive_messages.proto` (SetWorkflowEvent) |
| **Silo** | `Program.cs` (extracted config into extensions) |
| **Samples** | `maker/Program.cs` |
| **Demo** | `Aevatar.Demo.Cli/Program.cs` |
| **Tests** | `RuntimeAndContextTests.cs`, `HierarchyStreamingBddTests.cs`, `WorkflowIntegrationTests.cs`, `EventRoutingTests.cs`, `MakerRecursiveRegressionTests.cs`, `ConnectorCallIntegrationTests.cs` |
