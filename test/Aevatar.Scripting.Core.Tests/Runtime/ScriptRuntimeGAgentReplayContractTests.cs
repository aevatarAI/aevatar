using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptRuntimeGAgentReplayContractTests
{
    [Fact]
    public async Task HandleRunRequested_ShouldPersistDomainEvent_AndMutateViaTransitionOnly()
    {
        var definition = new ScriptDefinitionGAgent();
        definition.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
            new InMemoryEventStore());
        await definition.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-1",
            ScriptRevision = "rev-1",
            SourceText = """
using System.Collections.Generic;
public static class RuntimeContractScript
{
    public static IReadOnlyList<string> Decide(string inputJson) => new[] { "RuntimeContractEvent" };
}
""",
            SourceHash = "hash-1",
        });

        var agent = new ScriptRuntimeGAgent();
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
            new InMemoryEventStore());
        var services = new ServiceCollection();
        services.AddSingleton<IScriptAgentCompiler>(new RoslynScriptAgentCompiler(new ScriptSandboxPolicy()));
        services.AddSingleton<IActorRuntime>(new DefinitionOnlyRuntime(definition));
        agent.Services = services.BuildServiceProvider();

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-1",
            InputJson = "{}",
            ScriptRevision = "rev-1",
            DefinitionActorId = "definition-1",
        });

        agent.State.LastRunId.Should().Be("run-1");
        agent.State.Revision.Should().Be("rev-1");
        agent.State.DefinitionActorId.Should().Be("definition-1");
        agent.State.StatePayloadJson.Should().Contain("RuntimeContractEvent");
        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleRunRequested_ShouldCarryStatePayloadBetweenRuns_FromScriptResult()
    {
        var definition = new ScriptDefinitionGAgent();
        definition.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
            new InMemoryEventStore());
        await definition.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-stateful-1",
            ScriptRevision = "rev-stateful-1",
            SourceText = """
using System;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public static class StatefulRuntimeScript
{
    public static ScriptDecisionResult Decide(ScriptExecutionContext context)
    {
        var isFirstRun = string.IsNullOrWhiteSpace(context.CurrentStateJson);
        if (isFirstRun)
        {
            return new ScriptDecisionResult(
                new IMessage[] { new StringValue { Value = "FirstRunEvent" } },
                "{\"step\":1}");
        }

        return new ScriptDecisionResult(
            new IMessage[] { new StringValue { Value = "SecondRunEvent" } },
            "{\"step\":2}");
    }
}
""",
            SourceHash = "hash-stateful-1",
        });

        var agent = new ScriptRuntimeGAgent();
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
            new InMemoryEventStore());
        var services = new ServiceCollection();
        services.AddSingleton<IScriptAgentCompiler>(new RoslynScriptAgentCompiler(new ScriptSandboxPolicy()));
        services.AddSingleton<IActorRuntime>(new DefinitionOnlyRuntime(definition));
        agent.Services = services.BuildServiceProvider();

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-state-1",
            InputJson = "{}",
            ScriptRevision = "rev-stateful-1",
            DefinitionActorId = "definition-1",
        });

        agent.State.StatePayloadJson.Should().Be("{\"step\":1}");

        await agent.HandleRunScriptRequested(new RunScriptRequestedEvent
        {
            RunId = "run-state-2",
            InputJson = "{}",
            ScriptRevision = "rev-stateful-1",
            DefinitionActorId = "definition-1",
        });

        agent.State.StatePayloadJson.Should().Be("{\"step\":2}");
    }

    private sealed class DefinitionOnlyRuntime(ScriptDefinitionGAgent definition) : IActorRuntime
    {
        private readonly IActor _actor = new DefinitionActor(definition);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(string.Equals(id, "definition-1", StringComparison.Ordinal) ? _actor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(string.Equals(id, "definition-1", StringComparison.Ordinal));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DefinitionActor(ScriptDefinitionGAgent definition) : IActor
    {
        public string Id => "definition-1";
        public IAgent Agent => definition;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
