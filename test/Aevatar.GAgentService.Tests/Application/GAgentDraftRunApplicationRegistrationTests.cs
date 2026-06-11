using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunApplicationRegistrationTests
{
    [Fact]
    public async Task AddScopeGAgentDraftRunInteraction_ShouldExposeBusinessPortAndSharedRealtimeSession()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActorRuntime, RecordingActorRuntime>();
        services.AddSingleton<IActorDispatchPort, RecordingActorDispatchPort>();
        services.AddSingleton<IGAgentActorRegistryCommandPort, RecordingRegistryCommandPort>();
        services.AddSingleton<IScopeResourceAdmissionPort, AllowingAdmissionPort>();
        services.AddSingleton<IGAgentDraftRunProjectionPort, RecordingDraftRunProjectionPort>();
        services.AddSingleton<IGAgentRunTerminalProjectionPort, RecordingTerminalProjectionPort>();
        services.AddSingleton<IGAgentRunTerminalQueryPort, NoopTerminalQueryPort>();
        services.AddSingleton<IGAgentDraftRunObservationScopeActivationPort, RecordingActivationPort>();

        services.AddScopeGAgentDraftRunInteraction();

        services.Should().Contain(x =>
            x.ServiceType == typeof(ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus>) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(IRealtimeSession<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus>) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(DefaultCommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, AGUIEvent, GAgentDraftRunCompletionStatus>) &&
            x.ImplementationFactory != null);
        services.Should().Contain(x =>
            x.ServiceType == typeof(IGAgentDraftRunInteractionPort) &&
            x.ImplementationFactory != null);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = true,
        });

        var commandInteraction = provider.GetRequiredService<ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus>>();
        provider.GetRequiredService<IRealtimeSession<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus>>()
            .Should()
            .BeSameAs(commandInteraction);

        var port = provider.GetRequiredService<IGAgentDraftRunInteractionPort>();
        var emitted = new List<AGUIEvent>();
        var result = await port.ExecuteAsync(
            new GAgentDraftRunInteractionRequest(
                "scope-a",
                typeof(TestAgent).AssemblyQualifiedName!,
                "hello",
                PreferredActorId: "draft-actor"),
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            },
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt!.ActorId.Should().Be("draft-actor");
        emitted.Should().ContainSingle(x => x.EventCase == AGUIEvent.EventOneofCase.RunFinished);
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = string.IsNullOrWhiteSpace(id)
                ? Guid.NewGuid().ToString("N")
                : id.Trim();
            var actor = new TestActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            _actors.TryGetValue(id, out var actor);
            return Task.FromResult(actor);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingDraftRunProjectionPort : IGAgentDraftRunProjectionPort
    {
        public bool ProjectionEnabled => true;

        public Task<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?> AttachExistingActorProjectionAsync(
            string actorId,
            string commandId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            sink.Push(new AGUIEvent
            {
                RunFinished = new RunFinishedEvent
                {
                    ThreadId = actorId,
                    RunId = commandId,
                },
            });
            sink.Complete();
            return Task.FromResult<EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>?>(
                new EventSinkProjectionAttachment<IGAgentDraftRunProjectionLease>(
                    new DraftRunLease(actorId, commandId),
                    new NoopAsyncDisposable()));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IGAgentDraftRunProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(new NoopAsyncDisposable());

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(IGAgentDraftRunProjectionLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingTerminalProjectionPort : IGAgentRunTerminalProjectionPort
    {
        public Task<IGAgentRunTerminalProjectionLease?> AttachExistingProjectionAsync(
            string actorId,
            string correlationId,
            GAgentRunTerminalInteractionKind interactionKind,
            CancellationToken ct = default) =>
            Task.FromResult<IGAgentRunTerminalProjectionLease?>(new TerminalLease(actorId, correlationId, interactionKind));

        public Task ReleaseProjectionAsync(
            IGAgentRunTerminalProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopTerminalQueryPort : IGAgentRunTerminalQueryPort
    {
        public Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
            string actorId,
            string correlationId,
            CancellationToken ct = default) =>
            Task.FromResult<GAgentRunTerminalSnapshot?>(null);

        public Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult<GAgentRunTerminalSnapshot?>(null);
    }

    private sealed class RecordingActivationPort : IGAgentDraftRunObservationScopeActivationPort
    {
        public Task<GAgentDraftRunObservationScopeActivation?> ActivateAsync(
            string actorId,
            string commandId,
            string correlationId,
            CancellationToken ct = default) =>
            Task.FromResult<GAgentDraftRunObservationScopeActivation?>(
                new GAgentDraftRunObservationScopeActivation(actorId, commandId, correlationId));

        public Task ReleaseAsync(
            GAgentDraftRunObservationScopeActivation activation,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingRegistryCommandPort : IGAgentActorRegistryCommandPort
    {
        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionVisible));

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
    }

    private sealed class AllowingAdmissionPort : IScopeResourceAdmissionPort
    {
        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ScopeResourceAdmissionResult.Allowed());
    }

    private sealed record DraftRunLease(string ActorId, string CommandId) : IGAgentDraftRunProjectionLease;

    private sealed record TerminalLease(
        string ActorId,
        string CorrelationId,
        GAgentRunTerminalInteractionKind InteractionKind) : IGAgentRunTerminalProjectionLease;

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new TestAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TestAgent : IAgent
    {
        public string Id { get; } = "test-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
