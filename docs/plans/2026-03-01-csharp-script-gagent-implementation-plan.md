# C# Script GAgent Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement C# Script GAgent capability with strict EventEnvelope event handling, Event Sourcing replay consistency, unified Projection Pipeline, composition-only AI/GAgent reuse, and a business-meaningful multi-agent regression scenario.

**Architecture:** Build a new scripting vertical (`Abstractions -> Core -> Projection -> Hosting`) centered on `ScriptHostGAgent : GAgentBase<ScriptHostState>`. Application commands are adapted to requested events wrapped in `EventEnvelope`; ScriptHost handles only events and persists domain events. Read-side plugs into existing projection coordinator by exact `TypeUrl` routing. Script-to-agent calls/creation must go through runtime-backed ports (`IGAgentInvocationPort` / `IGAgentFactoryPort`) with `IActorRuntime` as lifecycle authority; IOC scope is not a lifecycle manager. Business scenario baseline is the insurance-claim anti-fraud workflow defined in `docs/plans/2026-03-01-multi-agent-script-ai-tdd-testcase.md`.

**Tech Stack:** .NET 9, xUnit, FluentAssertions, Google.Protobuf, existing Aevatar Foundation/CQRS/Workflow runtime and CI guards.

---

### Task 1: Scaffold Scripting Projects and Solution Wiring

**Files:**
- Create: `src/Aevatar.Scripting.Abstractions/Aevatar.Scripting.Abstractions.csproj`
- Create: `src/Aevatar.Scripting.Core/Aevatar.Scripting.Core.csproj`
- Create: `src/Aevatar.Scripting.Projection/Aevatar.Scripting.Projection.csproj`
- Create: `src/Aevatar.Scripting.Hosting/Aevatar.Scripting.Hosting.csproj`
- Create: `test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj`
- Modify: `aevatar.slnx`

**Step 1: Write the failing test**

```csharp
public class ScriptingProjectWiringTests
{
    [Fact]
    public void ScriptingAssemblies_ShouldBeLoadable()
    {
        typeof(Aevatar.Scripting.Core.ScriptHostGAgent).Assembly.Should().NotBeNull();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptingProjectWiringTests*" --nologo`
Expected: FAIL with missing projects/types.

**Step 3: Write minimal implementation**

```csharp
namespace Aevatar.Scripting.Core;
public sealed class ScriptHostGAgentPlaceholder {}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptingProjectWiringTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Abstractions src/Aevatar.Scripting.Core src/Aevatar.Scripting.Projection src/Aevatar.Scripting.Hosting test/Aevatar.Scripting.Core.Tests aevatar.slnx
git commit -m "chore: scaffold scripting projects and solution wiring"
```

### Task 2: Define Script Host Protobuf Contracts

**Files:**
- Create: `src/Aevatar.Scripting.Core/script_host_messages.proto`
- Test: `test/Aevatar.Scripting.Core.Tests/Contracts/ScriptHostProtoContractsTests.cs`

**Step 1: Write the failing test**

```csharp
public class ScriptHostProtoContractsTests
{
    [Fact]
    public void ScriptHostState_ShouldContainRevisionAndPayload()
    {
        var state = new ScriptHostState
        {
            ScriptId = "script-1",
            Revision = "r1",
            StatePayloadJson = "{}"
        };

        state.ScriptId.Should().Be("script-1");
        state.Revision.Should().Be("r1");
        state.StatePayloadJson.Should().Be("{}");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptHostProtoContractsTests*" --nologo`
Expected: FAIL with missing generated type.

**Step 3: Write minimal implementation**

```proto
syntax = "proto3";
package aevatar.scripting;
option csharp_namespace = "Aevatar.Scripting.Core";

message ScriptHostState {
  string script_id = 1;
  string revision = 2;
  string schema_hash = 3;
  string state_payload_json = 4;
  int64 last_applied_event_version = 5;
  string last_event_id = 6;
}

message RunScriptRequestedEvent {
  string run_id = 1;
  string input_json = 2;
  string script_revision = 3;
}

message ScriptDomainEventCommitted {
  string run_id = 1;
  string script_id = 2;
  string script_revision = 3;
  string event_type = 4;
  string payload_json = 5;
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptHostProtoContractsTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Core/script_host_messages.proto test/Aevatar.Scripting.Core.Tests/Contracts/ScriptHostProtoContractsTests.cs
git commit -m "feat: add script host protobuf contracts"
```

### Task 3: Add Scripting Abstractions and Definition Contract

**Files:**
- Create: `src/Aevatar.Scripting.Abstractions/GlobalUsings.cs`
- Create: `src/Aevatar.Scripting.Abstractions/Definitions/IScriptAgentDefinition.cs`
- Create: `src/Aevatar.Scripting.Abstractions/Definitions/ScriptExecutionContext.cs`
- Create: `src/Aevatar.Scripting.Abstractions/Definitions/ScriptDecisionResult.cs`
- Test: `test/Aevatar.Scripting.Core.Tests/Contract/ScriptDefinitionContractsTests.cs`

**Step 1: Write the failing test**

```csharp
public class ScriptDefinitionContractsTests
{
    [Fact]
    public async Task DecideAsync_ShouldReturnDomainEvents()
    {
        var definition = new FakeScriptDefinition();
        var result = await definition.DecideAsync(new ScriptExecutionContext("actor-1", "script-1", "r1"), CancellationToken.None);

        result.DomainEvents.Should().NotBeEmpty();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptDefinitionContractsTests*" --nologo`
Expected: FAIL with missing contract types.

**Step 3: Write minimal implementation**

```csharp
public interface IScriptAgentDefinition
{
    string ScriptId { get; }
    string Revision { get; }
    Task<ScriptDecisionResult> DecideAsync(ScriptExecutionContext context, CancellationToken ct);
}

public sealed record ScriptExecutionContext(string ActorId, string ScriptId, string Revision);
public sealed record ScriptDecisionResult(IReadOnlyList<IMessage> DomainEvents);
```

**Step 4: Run test to verify it passes**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptDefinitionContractsTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Abstractions test/Aevatar.Scripting.Core.Tests/Contract/ScriptDefinitionContractsTests.cs
git commit -m "feat: add script definition abstractions"
```

### Task 4: Implement Application Command Adapter (Command -> EventEnvelope)

**Files:**
- Create: `src/Aevatar.Scripting.Core/Application/RunScriptCommand.cs`
- Create: `src/Aevatar.Scripting.Core/Application/RunScriptCommandAdapter.cs`
- Test: `test/Aevatar.Scripting.Core.Tests/Application/RunScriptCommandAdapterTests.cs`

**Step 1: Write the failing test**

```csharp
public class RunScriptCommandAdapterTests
{
    [Fact]
    public void Map_ShouldProduce_EventEnvelope_With_RunScriptRequestedEvent()
    {
        var adapter = new RunScriptCommandAdapter();
        var envelope = adapter.Map(new RunScriptCommand("run-1", "{}", "r1"), "actor-1");

        envelope.Payload!.TypeUrl.Should().Contain("RunScriptRequestedEvent");
        envelope.Direction.Should().Be(EventDirection.Self);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*RunScriptCommandAdapterTests*" --nologo`
Expected: FAIL with missing adapter.

**Step 3: Write minimal implementation**

```csharp
public sealed record RunScriptCommand(string RunId, string InputJson, string ScriptRevision);

public sealed class RunScriptCommandAdapter
{
    public EventEnvelope Map(RunScriptCommand command, string actorId)
    {
        var payload = new RunScriptRequestedEvent
        {
            RunId = command.RunId,
            InputJson = command.InputJson,
            ScriptRevision = command.ScriptRevision
        };

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            PublisherId = actorId,
            Direction = EventDirection.Self,
            Payload = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow)
        };
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*RunScriptCommandAdapterTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Core/Application test/Aevatar.Scripting.Core.Tests/Application/RunScriptCommandAdapterTests.cs
git commit -m "feat: add application command to requested-event adapter"
```

### Task 5: Implement ScriptHostGAgent Event Handler and Replay-safe Transition

**Files:**
- Create: `src/Aevatar.Scripting.Core/ScriptHostGAgent.cs`
- Test: `test/Aevatar.Scripting.Core.Tests/Runtime/ScriptHostGAgentReplayContractTests.cs`

**Step 1: Write the failing test**

```csharp
public class ScriptHostGAgentReplayContractTests
{
    [Fact]
    public async Task HandleRequestedEvent_ShouldPersistDomainEvent_AndMutateViaTransitionOnly()
    {
        var agent = ScriptHostTestFactory.Create();

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent { RunId = "run-1", InputJson = "{}", ScriptRevision = "r1" });

        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
        agent.State.StatePayloadJson.Should().Contain("result");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptHostGAgentReplayContractTests*" --nologo`
Expected: FAIL with missing handler/transition.

**Step 3: Write minimal implementation**

```csharp
public sealed class ScriptHostGAgent : GAgentBase<ScriptHostState>
{
    [EventHandler]
    public async Task HandleRunScriptRequested(RunScriptRequestedEvent evt)
    {
        await PersistDomainEventAsync(new ScriptDomainEventCommitted
        {
            RunId = evt.RunId,
            ScriptId = State.ScriptId,
            ScriptRevision = evt.ScriptRevision,
            EventType = "script.run.completed",
            PayloadJson = "{\"result\":\"ok\"}"
        });
    }

    protected override ScriptHostState TransitionState(ScriptHostState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptDomainEventCommitted>((state, committed) =>
            {
                var next = state.Clone();
                next.Revision = committed.ScriptRevision;
                next.StatePayloadJson = committed.PayloadJson;
                next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
                return next;
            })
            .OrCurrent();
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptHostGAgentReplayContractTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Core/ScriptHostGAgent.cs test/Aevatar.Scripting.Core.Tests/Runtime/ScriptHostGAgentReplayContractTests.cs
git commit -m "feat: implement script host event handling and replay-safe transition"
```

### Task 6: Implement Script Compiler and Sandbox Policy

**Files:**
- Create: `src/Aevatar.Scripting.Core/Compilation/IScriptAgentCompiler.cs`
- Create: `src/Aevatar.Scripting.Core/Compilation/RoslynScriptAgentCompiler.cs`
- Create: `src/Aevatar.Scripting.Core/Compilation/ScriptSandboxPolicy.cs`
- Test: `test/Aevatar.Scripting.Core.Tests/Compilation/ScriptSandboxPolicyTests.cs`

**Step 1: Write the failing test**

```csharp
public class ScriptSandboxPolicyTests
{
    [Theory]
    [InlineData("Task.Run(() => 1);")]
    [InlineData("new Timer(_ => {}, null, 0, 1000);")]
    [InlineData("lock(obj){}")]
    public void Validate_ShouldRejectForbiddenApis(string source)
    {
        var policy = new ScriptSandboxPolicy();
        policy.Validate(source).IsValid.Should().BeFalse();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptSandboxPolicyTests*" --nologo`
Expected: FAIL with missing policy.

**Step 3: Write minimal implementation**

```csharp
public sealed class ScriptSandboxPolicy
{
    private static readonly string[] Forbidden = ["Task.Run(", "new Timer(", "new Thread(", "lock(", "Monitor.", "File.", "Directory."];

    public ScriptSandboxValidationResult Validate(string source)
    {
        var violations = Forbidden.Where(source.Contains).ToArray();
        return new ScriptSandboxValidationResult(violations.Length == 0, violations);
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptSandboxPolicyTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Core/Compilation test/Aevatar.Scripting.Core.Tests/Compilation/ScriptSandboxPolicyTests.cs
git commit -m "feat: add script compiler sandbox policy"
```

### Task 7: Implement Composition-only AI and Generic GAgent Invocation Ports

**Files:**
- Create: `src/Aevatar.Scripting.Core/AI/IAICapability.cs`
- Create: `src/Aevatar.Scripting.Core/AI/IRoleAgentPort.cs`
- Create: `src/Aevatar.Scripting.Core/AI/RoleAgentDelegateAICapability.cs`
- Create: `src/Aevatar.Scripting.Core/Ports/IGAgentInvocationPort.cs`
- Create: `src/Aevatar.Scripting.Hosting/Ports/RuntimeGAgentInvocationPort.cs`
- Test: `test/Aevatar.Scripting.Core.Tests/AI/RoleAgentDelegateAICapabilityTests.cs`
- Test: `test/Aevatar.Hosting.Tests/RuntimeGAgentInvocationPortTests.cs`

**Step 1: Write the failing tests**

```csharp
public class RoleAgentDelegateAICapabilityTests
{
    [Fact]
    public async Task AskAsync_ShouldDelegateToRolePort()
    {
        var capability = new RoleAgentDelegateAICapability(new FakeRoleAgentPort("ok"));
        var output = await capability.AskAsync("run-1", "hello", CancellationToken.None);
        output.Should().Be("ok");
    }
}

public class RuntimeGAgentInvocationPortTests
{
    [Fact]
    public async Task InvokeAsync_ShouldDispatchEventEnvelope_ToTargetActor()
    {
        // Arrange runtime + actor fake
        // Act invoke
        // Assert envelope target/payload/correlation
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*RoleAgentDelegateAICapabilityTests*" --nologo`
Expected: FAIL with missing adapter.

Run: `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --filter "*RuntimeGAgentInvocationPortTests*" --nologo`
Expected: FAIL with missing invocation port.

**Step 3: Write minimal implementation**

```csharp
public interface IAICapability
{
    Task<string> AskAsync(string runId, string prompt, CancellationToken ct);
}

public interface IRoleAgentPort
{
    Task<string> RunAsync(string runId, string prompt, CancellationToken ct);
}

public sealed class RoleAgentDelegateAICapability : IAICapability
{
    private readonly IRoleAgentPort _port;
    public RoleAgentDelegateAICapability(IRoleAgentPort port) => _port = port;
    public Task<string> AskAsync(string runId, string prompt, CancellationToken ct) => _port.RunAsync(runId, prompt, ct);
}

public interface IGAgentInvocationPort
{
    Task InvokeAsync(string targetAgentId, IMessage eventPayload, string correlationId, CancellationToken ct);
}

public sealed class RuntimeGAgentInvocationPort(IActorRuntime runtime) : IGAgentInvocationPort
{
    public async Task InvokeAsync(string targetAgentId, IMessage eventPayload, string correlationId, CancellationToken ct)
    {
        var actor = await runtime.GetAsync(targetAgentId)
            ?? throw new InvalidOperationException("Target GAgent not found.");
        await actor.HandleEventAsync(new EventEnvelope { TargetActorId = targetAgentId, CorrelationId = correlationId }, ct);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*RoleAgentDelegateAICapabilityTests*" --nologo`
Expected: PASS.

Run: `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --filter "*RuntimeGAgentInvocationPortTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Core/AI src/Aevatar.Scripting.Core/Ports src/Aevatar.Scripting.Hosting/Ports test/Aevatar.Scripting.Core.Tests/AI/RoleAgentDelegateAICapabilityTests.cs test/Aevatar.Hosting.Tests/RuntimeGAgentInvocationPortTests.cs
git commit -m "feat: add composition ai adapter and runtime gagent invocation port"
```

### Task 8: Add Script Projection Reducer and Projector

**Files:**
- Create: `src/Aevatar.Scripting.Projection/ReadModels/ScriptExecutionReadModel.cs`
- Create: `src/Aevatar.Scripting.Projection/Orchestration/ScriptProjectionContext.cs`
- Create: `src/Aevatar.Scripting.Projection/Reducers/ScriptEventReducerBase.cs`
- Create: `src/Aevatar.Scripting.Projection/Projectors/ScriptExecutionReadModelProjector.cs`
- Test: `test/Aevatar.CQRS.Projection.Core.Tests/ScriptExecutionReadModelProjectorTests.cs`

**Step 1: Write the failing test**

```csharp
public class ScriptExecutionReadModelProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldRouteByExactTypeUrl()
    {
        var fixture = ScriptProjectorFixture.Create();
        await fixture.ProjectAsync<ScriptDomainEventCommitted>();
        fixture.Mutated.Should().BeTrue();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --filter "*ScriptExecutionReadModelProjectorTests*" --nologo`
Expected: FAIL with missing script projector.

**Step 3: Write minimal implementation**

```csharp
public abstract class ScriptEventReducerBase<TEvent> : IProjectionEventReducer<ScriptExecutionReadModel, ScriptProjectionContext>
    where TEvent : class, IMessage<TEvent>, new()
{
    private static readonly string EventType = Any.Pack(new TEvent()).TypeUrl;
    public string EventTypeUrl => EventType;

    public bool Reduce(ScriptExecutionReadModel readModel, ScriptProjectionContext context, EventEnvelope envelope, DateTimeOffset now)
    {
        if (!string.Equals(envelope.Payload?.TypeUrl, EventType, StringComparison.Ordinal))
            return false;

        return ReduceTyped(readModel, context, envelope.Payload!.Unpack<TEvent>(), now);
    }

    protected abstract bool ReduceTyped(ScriptExecutionReadModel readModel, ScriptProjectionContext context, TEvent evt, DateTimeOffset now);
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --filter "*ScriptExecutionReadModelProjectorTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Projection test/Aevatar.CQRS.Projection.Core.Tests/ScriptExecutionReadModelProjectorTests.cs
git commit -m "feat: add script projection reducers and projector"
```

### Task 9: Wire Hosting/DI Composition

**Files:**
- Create: `src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Aevatar.Hosting/AevatarCapabilityHostExtensions.cs`
- Modify: `src/workflow/Aevatar.Workflow.Host.Api/Program.cs`
- Test: `test/Aevatar.Hosting.Tests/ScriptCapabilityHostExtensionsTests.cs`

**Step 1: Write the failing test**

```csharp
public class ScriptCapabilityHostExtensionsTests
{
    [Fact]
    public void AddScriptCapability_ShouldRegisterCoreServices()
    {
        var services = new ServiceCollection();
        services.AddScriptCapability();

        services.Any(x => x.ServiceType == typeof(IScriptAgentCompiler)).Should().BeTrue();
        services.Any(x => x.ServiceType == typeof(IAICapability)).Should().BeTrue();
        services.Any(x => x.ServiceType == typeof(IGAgentInvocationPort)).Should().BeTrue();
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --filter "*ScriptCapabilityHostExtensionsTests*" --nologo`
Expected: FAIL with missing extension.

**Step 3: Write minimal implementation**

```csharp
public static class ScriptCapabilityServiceCollectionExtensions
{
    public static IServiceCollection AddScriptCapability(this IServiceCollection services)
    {
        services.TryAddSingleton<IScriptAgentCompiler, RoslynScriptAgentCompiler>();
        services.TryAddSingleton<IGAgentInvocationPort, RuntimeGAgentInvocationPort>();
        services.TryAddSingleton<IAICapability, RoleAgentDelegateAICapability>();
        // IGAgentFactoryPort planned for lifecycle-authoritative create/destroy APIs.
        return services;
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --filter "*ScriptCapabilityHostExtensionsTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Hosting src/Aevatar.Hosting/AevatarCapabilityHostExtensions.cs src/workflow/Aevatar.Workflow.Host.Api/Program.cs test/Aevatar.Hosting.Tests/ScriptCapabilityHostExtensionsTests.cs
git commit -m "feat: wire script capability host composition"
```

### Task 10: Add CI Guard for Inheritance Boundary

**Files:**
- Create: `tools/ci/script_inheritance_guard.sh`
- Modify: `tools/ci/architecture_guards.sh`
- Test: `test/Aevatar.Scripting.Core.Tests/Architecture/ScriptInheritanceGuardTests.cs`

**Step 1: Write the failing test**

```csharp
public class ScriptInheritanceGuardTests
{
    [Fact]
    public void Pattern_ShouldDetectForbiddenInheritance()
    {
        const string line = "public class ScriptHostGAgent : AIGAgentBase<MyState>";
        line.Should().MatchRegex(@"ScriptHostGAgent\s*:\s*(RoleGAgent|AIGAgentBase<)");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*ScriptInheritanceGuardTests*" --nologo`
Expected: FAIL until guard wiring is complete.

**Step 3: Write minimal implementation**

```bash
#!/usr/bin/env bash
set -euo pipefail

if rg -n "class\s+ScriptHostGAgent\s*:\s*(RoleGAgent|AIGAgentBase<)" src; then
  echo "ScriptHostGAgent must not inherit RoleGAgent/AIGAgentBase."
  exit 1
fi
```

**Step 4: Run test to verify it passes**

Run: `bash tools/ci/script_inheritance_guard.sh`
Expected: PASS with no violations.

**Step 5: Commit**

```bash
git add tools/ci/script_inheritance_guard.sh tools/ci/architecture_guards.sh test/Aevatar.Scripting.Core.Tests/Architecture/ScriptInheritanceGuardTests.cs
git commit -m "chore: add script host inheritance boundary guard"
```

### Task 11: Add End-to-End Integration Coverage

**Files:**
- Create: `test/Aevatar.Integration.Tests/ScriptGAgentEndToEndTests.cs`
- Modify: `test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj`

**Step 1: Write the failing test**

```csharp
public class ScriptGAgentEndToEndTests
{
    [Fact]
    public async Task Run_ShouldFlow_CommandToEnvelope_ToActor_ToProjection()
    {
        var harness = await ScriptIntegrationHarness.StartAsync();
        await harness.RunCommandAsync("script-1", "run-1", "{}");

        var state = await harness.GetStateAsync("script-1");
        var readModel = await harness.GetReadModelAsync("script-1");

        state.StatePayloadJson.Should().Contain("result");
        readModel.Id.Should().Be("script-1");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "*ScriptGAgentEndToEndTests*" --nologo`
Expected: FAIL until full path wired.

**Step 3: Write minimal implementation**

```csharp
// Harness wiring
services.AddAevatarRuntime();
services.AddProjectionReadModelRuntime();
services.AddScriptCapability();
```

**Step 4: Run test to verify it passes**

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "*ScriptGAgentEndToEndTests*" --nologo`
Expected: PASS.

**Step 5: Commit**

```bash
git add test/Aevatar.Integration.Tests/ScriptGAgentEndToEndTests.cs test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj
git commit -m "test: add script gagent end-to-end event envelope coverage"
```

### Task 12: Documentation Sync and Final Verification

**Files:**
- Modify: `docs/architecture/csharp-script-gagent-requirements.md`
- Modify: `docs/architecture/csharp-script-gagent-detailed-architecture.md`
- Create: `docs/audit-scorecard/script-gagent-architecture-scorecard-2026-03-01.md`

**Step 1: Write failing manual checklist**

```text
- R-SG-01..R-SG-15 status updated
- Command/EventEnvelope boundary evidence added
- Inheritance guard evidence added
- Projection exact TypeUrl route evidence added
- Runtime lifecycle authority and Scope boundary evidence added
```

**Step 2: Run verification commands**

Run: `bash tools/ci/architecture_guards.sh`
Expected: PASS.

Run: `bash tools/ci/projection_route_mapping_guard.sh`
Expected: PASS.

Run: `dotnet build aevatar.slnx --nologo`
Expected: PASS.

Run: `dotnet test aevatar.slnx --nologo`
Expected: PASS.

**Step 3: Write minimal docs update**

```markdown
- Update requirement status matrix
- Add executed command evidence table
- Confirm "GAgent handles events only" is enforced
- Confirm "GAgent lifecycle is runtime-authoritative, not IOC-scope-authoritative"
```

**Step 4: Re-run guard subset**

Run: `bash tools/ci/architecture_guards.sh && bash tools/ci/projection_route_mapping_guard.sh`
Expected: PASS.

**Step 5: Commit**

```bash
git add docs/architecture/csharp-script-gagent-requirements.md docs/architecture/csharp-script-gagent-detailed-architecture.md docs/audit-scorecard/script-gagent-architecture-scorecard-2026-03-01.md
git commit -m "docs: finalize script gagent implementation evidence and verification"
```

### Task 12.5: Implement Runtime-authoritative GAgent Factory Port (Planned)

**Files:**
- Create: `src/Aevatar.Scripting.Core/Ports/IGAgentFactoryPort.cs`
- Create: `src/Aevatar.Scripting.Hosting/Ports/RuntimeGAgentFactoryPort.cs`
- Test: `test/Aevatar.Hosting.Tests/RuntimeGAgentFactoryPortTests.cs`
- Test: `test/Aevatar.Integration.Tests/ScriptGAgentFactoryLifecycleBoundaryTests.cs`

**Step 1: Write the failing tests**

```csharp
public class RuntimeGAgentFactoryPortTests
{
    [Fact]
    public async Task CreateAsync_ShouldDelegateToActorRuntime()
    {
        // Arrange fake runtime
        // Act create
        // Assert runtime.CreateAsync called
    }
}

public class ScriptGAgentFactoryLifecycleBoundaryTests
{
    [Fact]
    public async Task Lifecycle_ShouldBeRuntimeAuthoritative_NotScopeAuthoritative()
    {
        // Validate create/destroy/replay through runtime;
        // no direct service-provider-resolved GAgent instance lifecycle.
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --filter "*RuntimeGAgentFactoryPortTests*" --nologo`
Expected: FAIL with missing factory port.

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "*Script*Factory*Lifecycle*"`
Expected: FAIL with missing lifecycle-boundary coverage.

**Step 3: Write minimal implementation**

```csharp
public interface IGAgentFactoryPort
{
    Task<string> CreateAsync(Type agentType, string? actorId, CancellationToken ct);
    Task DestroyAsync(string actorId, CancellationToken ct);
    Task LinkAsync(string parentId, string childId, CancellationToken ct);
    Task UnlinkAsync(string childId, CancellationToken ct);
}

public sealed class RuntimeGAgentFactoryPort(IActorRuntime runtime) : IGAgentFactoryPort
{
    public async Task<string> CreateAsync(Type agentType, string? actorId, CancellationToken ct) =>
        (await runtime.CreateAsync(agentType, actorId, ct)).Id;

    public Task DestroyAsync(string actorId, CancellationToken ct) => runtime.DestroyAsync(actorId, ct);
    public Task LinkAsync(string parentId, string childId, CancellationToken ct) => runtime.LinkAsync(parentId, childId, ct);
    public Task UnlinkAsync(string childId, CancellationToken ct) => runtime.UnlinkAsync(childId, ct);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aevatar.Hosting.Tests/Aevatar.Hosting.Tests.csproj --filter "*RuntimeGAgentFactoryPortTests*" --nologo`
Expected: PASS.

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "*Script*Factory*Lifecycle*"`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/Aevatar.Scripting.Core/Ports src/Aevatar.Scripting.Hosting/Ports test/Aevatar.Hosting.Tests/RuntimeGAgentFactoryPortTests.cs test/Aevatar.Integration.Tests/ScriptGAgentFactoryLifecycleBoundaryTests.cs
git commit -m "feat: add runtime-authoritative gagent factory port and lifecycle boundary tests"
```

### Task 13: Add Business-meaningful Multi-agent Script TDD Scenario (Claim Anti-fraud)

**Files:**
- Create: `test/Aevatar.Scripting.Core.Tests/Business/ClaimScriptDecisionTests.cs`
- Create: `test/Aevatar.Scripting.Core.Tests/AI/ClaimRoleIntegrationTests.cs`
- Create: `test/Aevatar.Integration.Tests/ClaimOrchestrationIntegrationTests.cs`
- Create: `test/Aevatar.Integration.Tests/ClaimReplayTests.cs`
- Create: `test/Aevatar.CQRS.Projection.Core.Tests/ClaimReadModelProjectorTests.cs`
- Create: `test/Aevatar.Integration.Tests/ClaimLifecycleBoundaryTests.cs`
- Modify: `docs/plans/2026-03-01-multi-agent-script-ai-tdd-testcase.md`

**Step 1: Write the failing tests**

```csharp
public class ClaimScriptDecisionTests
{
    [Fact]
    public async Task Should_require_manual_review_when_high_risk()
    {
        // Arrange scenario input
        // Act script decision
        // Assert event sequence contains ClaimManualReviewRequestedEvent
    }
}

public class ClaimRoleIntegrationTests
{
    [Fact]
    public async Task Should_delegate_to_role_agent_capability_with_correlation()
    {
        // Assert run_id/correlation_id are propagated to IAICapability/IRoleAgentPort
    }
}

public class ClaimOrchestrationIntegrationTests
{
    [Fact]
    public async Task Should_call_agents_via_invocation_and_factory_ports_only()
    {
        // Assert no direct concrete-agent resolution path
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*Claim*"`
Expected: FAIL with missing scenario tests/implementations.

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "*Claim*"`
Expected: FAIL with missing orchestration/replay/lifecycle coverage.

Run: `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --filter "*Claim*"`
Expected: FAIL with missing read model projector tests.

**Step 3: Write minimal implementation**

```csharp
// 1) Add deterministic scenario fixtures for Case-A/B/C (Approve/ManualReview/Reject).
// 2) Wire RoleGAgent AI output into ClaimFactsExtractedEvent mapping.
// 3) Use IGAgentInvocationPort for FraudRisk/Compliance calls.
// 4) Use IGAgentFactoryPort only on high-risk branch to create HumanReviewGAgent.
// 5) Persist domain events and assert replay gives same state/read model.
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --filter "*Claim*"`
Expected: PASS.

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "*Claim*"`
Expected: PASS.

Run: `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --filter "*Claim*"`
Expected: PASS.

Run: `bash tools/ci/test_stability_guards.sh`
Expected: PASS.

**Step 5: Commit**

```bash
git add test/Aevatar.Scripting.Core.Tests/Business/ClaimScriptDecisionTests.cs test/Aevatar.Scripting.Core.Tests/AI/ClaimRoleIntegrationTests.cs test/Aevatar.Integration.Tests/ClaimOrchestrationIntegrationTests.cs test/Aevatar.Integration.Tests/ClaimReplayTests.cs test/Aevatar.CQRS.Projection.Core.Tests/ClaimReadModelProjectorTests.cs test/Aevatar.Integration.Tests/ClaimLifecycleBoundaryTests.cs docs/plans/2026-03-01-multi-agent-script-ai-tdd-testcase.md
git commit -m "test: add claim anti-fraud multi-agent script tdd scenario"
```

## Cross-Task Rules

1. Use `@test-driven-development` in every task.
2. Use `@verification-before-completion` before claiming milestone completion.
3. Keep one task per commit and keep tests green.
4. Do not introduce `TypeUrl.Contains(...)`.
5. Do not let any GAgent process Command directly.
6. Do not let `ScriptHostGAgent` inherit `RoleGAgent` or `AIGAgentBase<TState>`.
7. Do not resolve concrete GAgent instances from `IServiceProvider` in script execution path.
8. Ensure create/destroy/link/unlink/restore operations go through `IActorRuntime`.

## Milestones

1. M1: Contracts and projects ready (Tasks 1-3)
2. M2: Application adapter + host write path + sandbox (Tasks 4-6)
3. M3: AI composition + generic invocation + projection integration (Tasks 7-8)
4. M4: Hosting/guards/e2e/docs complete and lifecycle boundary planned (Tasks 9-12)
5. M5: Claim anti-fraud multi-agent business scenario integrated into TDD regression (Task 13)
