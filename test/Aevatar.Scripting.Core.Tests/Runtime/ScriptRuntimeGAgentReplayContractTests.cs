using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptBehaviorGAgentReplayContractTests
{
    [Fact]
    public async Task HandleEnvelopeAsync_ShouldPersistCommittedFact_AndMutateViaTransitionOnly()
    {
        var harness = CreateRuntimeHarness();

        await BindAsync(harness.Agent, harness.SourceHash);
        await RunAsync(harness.Agent, "run-1", "rev-1", "runtime.requested");

        harness.Agent.State.DefinitionActorId.Should().Be("definition-1");
        harness.Agent.State.ScriptId.Should().Be("script-1");
        harness.Agent.State.Revision.Should().Be("rev-1");
        harness.Agent.State.LastRunId.Should().Be("run-1");
        harness.Agent.State.LastAppliedEventVersion.Should().Be(2);
        harness.Agent.State.StateRoot.Should().NotBeNull();
        harness.Agent.State.StateRoot.Unpack<ScriptProfileState>().CommandCount.Should().Be(1);
        harness.Agent.State.LastEventId.Should().Be(Any.Pack(new ScriptProfileUpdated()).TypeUrl);
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldCarryStateAcrossRuns_ByApplyDomainEvent()
    {
        var harness = CreateRuntimeHarness();

        await BindAsync(harness.Agent, harness.SourceHash);
        await RunAsync(harness.Agent, "run-1", "rev-1", "runtime.requested");
        await RunAsync(harness.Agent, "run-2", "rev-1", "runtime.requested");

        harness.Agent.State.LastRunId.Should().Be("run-2");
        harness.Agent.State.LastAppliedEventVersion.Should().Be(3);
        harness.Agent.State.StateRoot.Should().NotBeNull();
        harness.Agent.State.StateRoot.Unpack<ScriptProfileState>().CommandCount.Should().Be(2);
    }

    [Fact]
    public async Task HandleEnvelopeAsync_ShouldRejectRun_WhenRequestedRevisionDiffersFromBinding()
    {
        var harness = CreateRuntimeHarness();
        await BindAsync(harness.Agent, harness.SourceHash);

        var act = () => harness.Agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = "run-mismatch",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-2",
            RequestedEventType = "runtime.requested",
            InputPayload = Any.Pack(BuildCommand("run-mismatch")),
        }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bound to revision*rev-1*rev-2*");
    }

    private static async Task BindAsync(ScriptBehaviorGAgent agent, string sourceHash)
    {
        await agent.HandleEnvelopeAsync(BuildEnvelope(new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = StatefulBehaviorSource,
            SourceHash = sourceHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(StatefulBehaviorSource),
            StateTypeUrl = Any.Pack(new ScriptProfileState()).TypeUrl,
            ReadModelTypeUrl = Any.Pack(new ScriptProfileReadModel()).TypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        }));
    }

    private static async Task RunAsync(
        ScriptBehaviorGAgent agent,
        string runId,
        string revision,
        string requestedEventType)
    {
        await agent.HandleEnvelopeAsync(BuildEnvelope(new RunScriptRequestedEvent
        {
            RunId = runId,
            DefinitionActorId = "definition-1",
            ScriptRevision = revision,
            RequestedEventType = requestedEventType,
            InputPayload = Any.Pack(BuildCommand(runId)),
        }));
    }

    private static ScriptProfileUpdateCommand BuildCommand(string runId) =>
        new()
        {
            CommandId = "command-" + runId,
            ActorId = "actor-" + runId,
            PolicyId = "policy-1",
            InputText = " runtime requested ",
            Tags = { "runtime", runId },
        };

    private static EventEnvelope BuildEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("runtime-test", TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-1",
            },
        };

    private static RuntimeHarness CreateRuntimeHarness()
    {
        var compiler = new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy());
        var artifactResolver = new CachedScriptBehaviorArtifactResolver(compiler);
        var codec = new ProtobufMessageCodec();
        var dispatcher = new Aevatar.Scripting.Application.Runtime.ScriptBehaviorDispatcher(
            artifactResolver,
            new ScriptReadModelMaterializationCompiler(),
            new ScriptNativeProjectionBuilder(),
            codec);
        var publisher = new RecordingEventPublisher();
        var agent = new ScriptBehaviorGAgent(dispatcher, new StaticCapabilityFactory(), artifactResolver, new ScriptReadModelMaterializationCompiler(), codec)
        {
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptBehaviorState>(
                new InMemoryEventStore()),
        };

        return new RuntimeHarness(agent, publisher, ComputeSourceHash(StatefulBehaviorSource));
    }

    private static string ComputeSourceHash(string source)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record RuntimeHarness(
        ScriptBehaviorGAgent Agent,
        RecordingEventPublisher Publisher,
        string SourceHash);

    private sealed class StaticCapabilityFactory : IScriptBehaviorRuntimeCapabilityFactory
    {
        public IScriptBehaviorRuntimeCapabilities Create(
            ScriptBehaviorRuntimeCapabilityContext context,
            Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
            Func<string, IMessage, CancellationToken, Task> sendToAsync,
            Func<IMessage, CancellationToken, Task> publishToSelfAsync,
            Func<string, TimeSpan, IMessage, CancellationToken, Task<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease>> scheduleSelfSignalAsync,
            Func<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease, CancellationToken, Task> cancelCallbackAsync)
        {
            _ = context;
            _ = publishAsync;
            _ = sendToAsync;
            _ = publishToSelfAsync;
            _ = scheduleSelfSignalAsync;
            _ = cancelCallbackAsync;
            return new NoOpCapabilities();
        }
    }

    private sealed class NoOpCapabilities : IScriptBehaviorRuntimeCapabilities
    {
        public Task<string> AskAIAsync(string prompt, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct) => Task.CompletedTask;
        public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct) => Task.CompletedTask;
        public Task<Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(string callbackId, TimeSpan dueTime, IMessage eventPayload, CancellationToken ct) =>
            Task.FromResult(new Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease("runtime-1", callbackId, 0, Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackBackend.InMemory));
        public Task CancelDurableCallbackAsync(Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease lease, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) => Task.FromResult(actorId ?? string.Empty);
        public Task DestroyAgentAsync(string actorId, CancellationToken ct) => Task.CompletedTask;
        public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) => Task.CompletedTask;
        public Task<ScriptReadModelSnapshot?> GetReadModelSnapshotAsync(string actorId, CancellationToken ct) => Task.FromResult<ScriptReadModelSnapshot?>(null);
        public Task<Any?> ExecuteReadModelQueryAsync(string actorId, Any queryPayload, CancellationToken ct) => Task.FromResult<Any?>(null);
        public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(ScriptEvolutionProposal proposal, CancellationToken ct) =>
            Task.FromResult(new ScriptPromotionDecision(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, new ScriptEvolutionValidationReport(false, [])));
        public Task<string> UpsertScriptDefinitionAsync(string scriptId, string scriptRevision, string sourceText, string sourceHash, string? definitionActorId, CancellationToken ct) =>
            Task.FromResult(definitionActorId ?? string.Empty);
        public Task<string> SpawnScriptRuntimeAsync(string definitionActorId, string scriptRevision, string? runtimeActorId, CancellationToken ct) =>
            Task.FromResult(runtimeActorId ?? string.Empty);
        public Task RunScriptInstanceAsync(string runtimeActorId, string runId, Any? inputPayload, string scriptRevision, string definitionActorId, string requestedEventType, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PromoteRevisionAsync(string catalogActorId, string scriptId, string revision, string definitionActorId, string sourceHash, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task RollbackRevisionAsync(string catalogActorId, string scriptId, string targetRevision, string reason, string proposalId, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = audience;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Sent.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            _ = sourceEnvelope;
            _ = options;
            ct.ThrowIfCancellationRequested();
            Sent.Add(evt);
            return Task.CompletedTask;
        }
    }

    private const string StatefulBehaviorSource =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Scripting.Core.Tests.Messages;

        public sealed class StatefulBehavior : ScriptBehavior<ScriptProfileState, ScriptProfileReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<ScriptProfileState, ScriptProfileReadModel> builder)
            {
                builder
                    .OnCommand<ScriptProfileUpdateCommand>(HandleCommandAsync)
                    .OnEvent<ScriptProfileUpdated>(
                        apply: static (state, evt, _) => new ScriptProfileState
                        {
                            CommandCount = (state?.CommandCount ?? 0) + 1,
                            ActorId = evt.Current?.ActorId ?? string.Empty,
                            PolicyId = evt.Current?.PolicyId ?? string.Empty,
                            LastCommandId = evt.CommandId ?? string.Empty,
                            InputText = evt.Current?.InputText ?? string.Empty,
                            NormalizedText = evt.Current?.NormalizedText ?? string.Empty,
                            Tags = { evt.Current == null ? global::System.Array.Empty<string>() : (global::System.Collections.Generic.IEnumerable<string>)evt.Current.Tags },
                        })
                    .ProjectState(static (state, _) => state == null
                        ? new ScriptProfileReadModel()
                        : new ScriptProfileReadModel
                        {
                            HasValue = true,
                            ActorId = state.ActorId,
                            PolicyId = state.PolicyId,
                            LastCommandId = state.LastCommandId,
                            InputText = state.InputText,
                            NormalizedText = state.NormalizedText,
                            Search = new ScriptProfileSearchIndex
                            {
                                LookupKey = $"{state.ActorId}:{state.PolicyId}".ToLowerInvariant(),
                                SortKey = state.NormalizedText ?? string.Empty,
                            },
                            Refs = new ScriptProfileDocumentRef
                            {
                                ActorId = state.ActorId ?? string.Empty,
                                PolicyId = state.PolicyId ?? string.Empty,
                            },
                            Tags = { state.Tags },
                        });
            }

            private static Task HandleCommandAsync(
                ScriptProfileUpdateCommand inbound,
                ScriptCommandContext<ScriptProfileState> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var evt = new ScriptProfileUpdated
                {
                    CommandId = inbound.CommandId ?? string.Empty,
                    Current = new ScriptProfileReadModel
                    {
                        HasValue = true,
                        ActorId = inbound.ActorId ?? string.Empty,
                        PolicyId = inbound.PolicyId ?? string.Empty,
                        LastCommandId = inbound.CommandId ?? string.Empty,
                        InputText = inbound.InputText ?? string.Empty,
                        NormalizedText = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                        Search = new ScriptProfileSearchIndex
                        {
                            LookupKey = $"{inbound.ActorId}:{inbound.PolicyId}".ToLowerInvariant(),
                            SortKey = (inbound.InputText ?? string.Empty).Trim().ToUpperInvariant(),
                        },
                        Refs = new ScriptProfileDocumentRef
                        {
                            ActorId = inbound.ActorId ?? string.Empty,
                            PolicyId = inbound.PolicyId ?? string.Empty,
                        },
                    },
                };
                evt.Current.Tags.AddRange(inbound.Tags.Select(static tag => tag.Trim().ToLowerInvariant()));
                context.Emit(evt);
                return Task.CompletedTask;
            }

            private static Task<ScriptProfileQueryResponded?> HandleQueryAsync(
                ScriptProfileQueryRequested queryPayload,
                ScriptQueryContext<ScriptProfileReadModel> snapshot,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ScriptProfileQueryResponded?>(new ScriptProfileQueryResponded
                {
                    RequestId = queryPayload.RequestId ?? string.Empty,
                    Current = snapshot.CurrentReadModel ?? new ScriptProfileReadModel(),
                });
            }
        }
        """;
}
