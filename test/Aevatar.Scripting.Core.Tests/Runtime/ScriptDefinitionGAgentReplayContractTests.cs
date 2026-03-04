using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Schema;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptDefinitionGAgentReplayContractTests
{
    [Fact]
    public async Task HandleUpsertRequested_ShouldPersistDefinitionEvent_AndMutateViaTransitionOnly()
    {
        var agent = CreateAgent();
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
            new InMemoryEventStore());

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-1",
            ScriptRevision = "rev-1",
            SourceText = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class DefinitionReplayScript : IScriptPackageRuntime, IScriptContractProvider
{
    public ScriptContractManifest ContractManifest => new(
        "claim_case_v1",
        new[] { "ClaimApprovedEvent" },
        "claim_runtime_state_v1",
        "claim_case_readmodel_v3",
        new ScriptReadModelDefinition(
            "claim_case",
            "3",
            new[]
            {
                new ScriptReadModelFieldDefinition("claim_case_id", "keyword", "claim_case_id", false),
            },
            new[]
            {
                new ScriptReadModelIndexDefinition("idx_claim_case_id", new[] { "claim_case_id" }, true, "elasticsearch"),
            },
            new[]
            {
                new ScriptReadModelRelationDefinition(
                    "rel_policy",
                    "policy_id",
                    "policy",
                    "policy_id",
                    "many_to_one",
                    "neo4j"),
            }),
        new[] { "elasticsearch", "neo4j" });

    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct) =>
        Task.FromResult(new ScriptHandlerResult(System.Array.Empty<IMessage>()));

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        System.Collections.Generic.IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        System.Collections.Generic.IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""",
            SourceHash = "hash-1",
        });

        agent.State.ScriptId.Should().Be("script-1");
        agent.State.Revision.Should().Be("rev-1");
        agent.State.SourceText.Should().Contain("DefinitionReplayScript");
        agent.State.SourceHash.Should().Be("hash-1");
        agent.State.ReadModelSchemaVersion.Should().Be("3");
        agent.State.ReadModelSchema.Should().NotBeNull();
        agent.State.ReadModelSchemaHash.Should().NotBeNullOrWhiteSpace();
        agent.State.ReadModelSchemaStoreKinds.Should().Contain("elasticsearch");
        agent.State.ReadModelSchemaStatus.Should().Be("validated");
        agent.State.ReadModelSchemaFailureReason.Should().BeEmpty();
        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleUpsertRequested_ShouldMarkSchemaActivationFailed_WhenRequiredStoreKindMissing()
    {
        var agent = CreateAgent(
            new DefaultScriptReadModelSchemaActivationPolicy([ScriptReadModelStoreKind.Document]));
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
            new InMemoryEventStore());

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-unsupported",
            ScriptRevision = "rev-unsupported-1",
            SourceText = """
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

public sealed class UnsupportedSchemaReplayScript : IScriptPackageRuntime, IScriptContractProvider
{
    public ScriptContractManifest ContractManifest => new(
        "claim_case_v1",
        new[] { "ClaimApprovedEvent" },
        "claim_runtime_state_v1",
        "claim_case_readmodel_v1",
        new ScriptReadModelDefinition(
            "claim_case",
            "1",
            new[]
            {
                new ScriptReadModelFieldDefinition("claim_case_id", "keyword", "claim_case_id", false),
            },
            new[]
            {
                new ScriptReadModelIndexDefinition("idx_claim_case_id", new[] { "claim_case_id" }, true, "document"),
            },
            new[]
            {
                new ScriptReadModelRelationDefinition(
                    "rel_policy",
                    "policy_id",
                    "policy",
                    "policy_id",
                    "many_to_one",
                    "graph"),
            }),
        new[] { "document", "graph" });

    public Task<ScriptHandlerResult> HandleRequestedEventAsync(
        ScriptRequestedEventEnvelope requestedEvent,
        ScriptExecutionContext context,
        CancellationToken ct) =>
        Task.FromResult(new ScriptHandlerResult(System.Array.Empty<IMessage>()));

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
        System.Collections.Generic.IReadOnlyDictionary<string, Any> currentState,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentState);

    public ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
        System.Collections.Generic.IReadOnlyDictionary<string, Any> currentReadModel,
        ScriptDomainEventEnvelope domainEvent,
        CancellationToken ct) =>
        ValueTask.FromResult<System.Collections.Generic.IReadOnlyDictionary<string, Any>?>(currentReadModel);
}
""",
            SourceHash = "hash-unsupported-1",
        });

        agent.State.ScriptId.Should().Be("script-unsupported");
        agent.State.Revision.Should().Be("rev-unsupported-1");
        agent.State.ReadModelSchemaVersion.Should().Be("1");
        agent.State.ReadModelSchemaStatus.Should().Be("activation_failed");
        agent.State.ReadModelSchemaFailureReason.Should().Contain("Graph");
        agent.State.LastAppliedEventVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleUpsertRequested_ShouldDisposeCompiledDefinition_WhenCompilerReturnsAsyncDisposableDefinition()
    {
        var definition = new DisposableTrackingDefinition();
        var agent = new ScriptDefinitionGAgent(
            new DisposableTrackingCompiler(definition),
            new DefaultScriptReadModelSchemaActivationPolicy())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptDefinitionState>(
                new InMemoryEventStore()),
        };

        await agent.HandleUpsertScriptDefinitionRequested(new UpsertScriptDefinitionRequestedEvent
        {
            ScriptId = "script-dispose",
            ScriptRevision = "rev-1",
            SourceText = "public sealed class PlaceholderScript {}",
            SourceHash = "hash-dispose",
        });

        definition.IsDisposed.Should().BeTrue();
    }

    private static ScriptDefinitionGAgent CreateAgent(
        IScriptReadModelSchemaActivationPolicy? activationPolicy = null)
    {
        var policy = activationPolicy ?? new DefaultScriptReadModelSchemaActivationPolicy();
        return new ScriptDefinitionGAgent(
            new RoslynScriptPackageCompiler(new ScriptSandboxPolicy()),
            policy);
    }

    private sealed class DisposableTrackingCompiler(DisposableTrackingDefinition definition) : IScriptPackageCompiler
    {
        private readonly DisposableTrackingDefinition _definition = definition;

        public Task<ScriptPackageCompilationResult> CompileAsync(
            ScriptPackageCompilationRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ScriptPackageCompilationResult(
                    IsSuccess: true,
                    CompiledDefinition: _definition,
                    ContractManifest: new ScriptContractManifest("input", [], "state", "readmodel"),
                    Diagnostics: Array.Empty<string>()));
        }
    }

    private sealed class DisposableTrackingDefinition : IScriptPackageDefinition, IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }
        public string ScriptId => "script-dispose";
        public string Revision => "rev-1";
        public ScriptContractManifest ContractManifest { get; } =
            new("input", [], "state", "readmodel");

        public Task<ScriptHandlerResult> HandleRequestedEventAsync(
            ScriptRequestedEventEnvelope requestedEvent,
            ScriptExecutionContext context,
            CancellationToken ct)
        {
            _ = requestedEvent;
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptHandlerResult(Array.Empty<IMessage>()));
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ApplyDomainEventAsync(
            IReadOnlyDictionary<string, Any> currentState,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = domainEvent;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentState);
        }

        public ValueTask<IReadOnlyDictionary<string, Any>?> ReduceReadModelAsync(
            IReadOnlyDictionary<string, Any> currentReadModel,
            ScriptDomainEventEnvelope domainEvent,
            CancellationToken ct)
        {
            _ = domainEvent;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyDictionary<string, Any>?>(currentReadModel);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
