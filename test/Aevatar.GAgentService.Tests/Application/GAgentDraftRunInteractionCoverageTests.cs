using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.Presentation.AGUI;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunInteractionCoverageTests
{
    [Fact]
    public async Task Resolver_ShouldReturnUnknownActorType_WhenTypeCannotBeResolved()
    {
        var resolver = new GAgentDraftRunCommandTargetResolver(
            new DraftRunStubActorRuntime(),
            new DraftRunProjectionPort());

        var result = await resolver.ResolveAsync(
            new GAgentDraftRunCommand("scope-a", "missing-type", "hello"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.UnknownActorType);
    }

    [Fact]
    public async Task Resolver_ShouldCreatePreferredActor_WhenMissing()
    {
        var runtime = new DraftRunStubActorRuntime();
        var resolver = new GAgentDraftRunCommandTargetResolver(
            runtime,
            new DraftRunProjectionPort());

        var result = await resolver.ResolveAsync(
            new GAgentDraftRunCommand(
                "scope-a",
                typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
                "hello",
                PreferredActorId: "preferred-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].actorId.Should().Be("preferred-1");
        result.Target!.ActorId.Should().Be("preferred-1");
        result.Target.ActorTypeName.Should().Be(typeof(DraftRunExpectedAgent).AssemblyQualifiedName!);
    }

    [Fact]
    public async Task CommandTargetCleanup_ShouldDetachReleaseAndDisposeBoundObservation()
    {
        var projectionPort = new DraftRunProjectionPort();
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort);
        var lease = new DraftRunProjectionLease("actor-1", "cmd-1");
        var sink = new DraftRunRecordingSink();
        target.BindLiveObservation(lease, sink);

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        projectionPort.DetachCalls.Should().ContainSingle(x => ReferenceEquals(x.lease, lease));
        projectionPort.ReleaseCalls.Should().ContainSingle(x => ReferenceEquals(x, lease));
        sink.Completed.Should().BeTrue();
        sink.DisposeCalls.Should().Be(1);
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task Binder_ShouldThrow_WhenProjectionPipelineIsUnavailable()
    {
        var projectionPort = new DraftRunProjectionPort { LeaseToReturn = null };
        var binder = new GAgentDraftRunCommandTargetBinder(projectionPort);
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
            projectionPort);

        var act = async () => await binder.BindAsync(
            new GAgentDraftRunCommand("scope-a", typeof(DraftRunExpectedAgent).AssemblyQualifiedName!, "hello"),
            target,
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("GAgent draft-run projection pipeline is unavailable.");
    }

    [Fact]
    public void EnvelopeFactory_ShouldMapMetadataInputPartsAndSessionFallback()
    {
        var factory = new GAgentDraftRunCommandEnvelopeFactory();

        var envelope = factory.CreateEnvelope(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                ActorTypeName: typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
                Prompt: "hello",
                SessionId: " ",
                NyxIdAccessToken: " token ",
                ModelOverride: " model-x ",
                PreferredLlmRoute: " /route ",
                InputParts:
                [
                    new GAgentDraftRunInputPart
                    {
                        Kind = GAgentDraftRunInputPartKind.Text,
                        Text = "body",
                        Name = "p1",
                    },
                    new GAgentDraftRunInputPart
                    {
                        Kind = GAgentDraftRunInputPartKind.Image,
                        Uri = "https://example.com/image.png",
                        MediaType = "image/png",
                    },
                ]),
            new CommandContext(
                "actor-1",
                "cmd-1",
                "corr-1",
                new Dictionary<string, string>
                {
                    [" x-trace "] = " trace-1 ",
                    [" "] = "ignored",
                    ["empty"] = " ",
                }));

        var payload = envelope.Payload.Unpack<ChatRequestEvent>();
        payload.Prompt.Should().Be("hello");
        payload.ScopeId.Should().Be("scope-a");
        payload.SessionId.Should().Be("corr-1");
        payload.Metadata["x-trace"].Should().Be("trace-1");
        payload.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken].Should().Be("token");
        payload.Metadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("model-x");
        payload.Metadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().Be("/route");
        payload.InputParts.Should().HaveCount(2);
        payload.InputParts[0].Kind.Should().Be(ChatContentPartKind.Text);
        payload.InputParts[0].Text.Should().Be("body");
        payload.InputParts[1].Kind.Should().Be(ChatContentPartKind.Image);
        payload.InputParts[1].Uri.Should().Be("https://example.com/image.png");
        envelope.Route.GetTargetActorId().Should().Be("actor-1");
        envelope.Propagation.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public void EnvelopeFactory_ShouldLeaveSessionEmpty_WhenFallbackIsDisabled()
    {
        var factory = new GAgentDraftRunCommandEnvelopeFactory();

        var envelope = factory.CreateEnvelope(
            new GAgentDraftRunCommand(
                ScopeId: "scope-a",
                ActorTypeName: typeof(DraftRunExpectedAgent).AssemblyQualifiedName!,
                Prompt: "hello",
                SessionId: null,
                UseCorrelationIdAsFallbackSessionId: false),
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        envelope.Payload.Unpack<ChatRequestEvent>().SessionId.Should().BeEmpty();
    }

    [Fact]
    public async Task ReceiptFactoryCompletionPolicyFinalizeEmitterAndDurableResolver_ShouldBehaveAsExpected()
    {
        var target = new GAgentDraftRunCommandTarget(
            new DraftRunStubActor("actor-1", new DraftRunExpectedAgent()),
            "actor-type",
            new DraftRunProjectionPort());
        var receiptFactory = new GAgentDraftRunAcceptedReceiptFactory();
        var receipt = receiptFactory.Create(
            target,
            new CommandContext("actor-1", "cmd-1", "corr-1", new Dictionary<string, string>()));

        receipt.Should().Be(new GAgentDraftRunAcceptedReceipt("actor-1", "actor-type", "cmd-1", "corr-1"));

        var completionPolicy = new GAgentDraftRunCompletionPolicy();
        completionPolicy.TryResolve(new AGUIEvent { TextMessageEnd = new Aevatar.Presentation.AGUI.TextMessageEndEvent() }, out var textCompletion)
            .Should().BeTrue();
        textCompletion.Should().Be(GAgentDraftRunCompletionStatus.TextMessageCompleted);
        completionPolicy.TryResolve(new AGUIEvent { RunFinished = new RunFinishedEvent() }, out var finishedCompletion)
            .Should().BeTrue();
        finishedCompletion.Should().Be(GAgentDraftRunCompletionStatus.RunFinished);
        completionPolicy.TryResolve(new AGUIEvent { RunError = new RunErrorEvent { Message = "boom" } }, out var failedCompletion)
            .Should().BeTrue();
        failedCompletion.Should().Be(GAgentDraftRunCompletionStatus.Failed);
        completionPolicy.TryResolve(new AGUIEvent(), out var unknownCompletion).Should().BeFalse();
        unknownCompletion.Should().Be(GAgentDraftRunCompletionStatus.Unknown);

        var emitter = new GAgentDraftRunFinalizeEmitter();
        var emitted = new List<AGUIEvent>();
        await emitter.EmitAsync(
            receipt,
            GAgentDraftRunCompletionStatus.TextMessageCompleted,
            completed: true,
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        emitted.Should().ContainSingle();
        emitted[0].RunFinished.ThreadId.Should().Be("actor-1");
        emitted[0].RunFinished.RunId.Should().Be("cmd-1");

        var durableResolver = new GAgentDraftRunDurableCompletionResolver();
        (await durableResolver.ResolveAsync(receipt, CancellationToken.None))
            .Should().Be(CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete);
    }

    private sealed class DraftRunProjectionPort : IGAgentDraftRunProjectionPort
    {
        public DraftRunProjectionLease? LeaseToReturn { get; init; } = new("actor-1", "cmd-1");
        public bool ProjectionEnabled => true;
        public List<(string actorId, string commandId)> EnsureCalls { get; } = [];
        public List<(IGAgentDraftRunProjectionLease lease, IEventSink<AGUIEvent> sink)> AttachCalls { get; } = [];
        public List<(IGAgentDraftRunProjectionLease lease, IEventSink<AGUIEvent> sink)> DetachCalls { get; } = [];
        public List<IGAgentDraftRunProjectionLease> ReleaseCalls { get; } = [];

        public Task<IGAgentDraftRunProjectionLease?> EnsureActorProjectionAsync(
            string actorId,
            string commandId,
            CancellationToken ct = default)
        {
            EnsureCalls.Add((actorId, commandId));
            return Task.FromResult<IGAgentDraftRunProjectionLease?>(LeaseToReturn);
        }

        public Task AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            AttachCalls.Add((lease, sink));
            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            DetachCalls.Add((lease, sink));
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IGAgentDraftRunProjectionLease lease,
            CancellationToken ct = default)
        {
            ReleaseCalls.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed record DraftRunProjectionLease(string ActorId, string CommandId) : IGAgentDraftRunProjectionLease;

    private sealed class DraftRunStubActorRuntime(params IActor[] actors) : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = actors.ToDictionary(x => x.Id, StringComparer.Ordinal);
        public List<(Type agentType, string? actorId)> CreateCalls { get; } = [];

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            CreateCalls.Add((agentType, actorId));
            var actor = new DraftRunStubActor(actorId, (IAgent)Activator.CreateInstance(agentType)!);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DraftRunStubActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class DraftRunExpectedAgent : IAgent
    {
        public string Id => "draft-run-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DraftRunRecordingSink : IEventSink<AGUIEvent>
    {
        public bool Completed { get; private set; }
        public int DisposeCalls { get; private set; }

        public void Push(AGUIEvent evt)
        {
        }

        public ValueTask PushAsync(AGUIEvent evt, CancellationToken ct = default) => ValueTask.CompletedTask;

        public void Complete() => Completed = true;

        public async IAsyncEnumerable<AGUIEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
