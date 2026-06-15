using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunObservationScopeActivationTests
{
    [Fact]
    public void CommandContextSeed_ShouldExposeTypedCommandAndCorrelationSeeds()
    {
        var command = new GAgentDraftRunCommand(
            "scope-a",
            typeof(TestAgent).AssemblyQualifiedName!,
            "hello",
            CommandIdSeed: "cmd-seed",
            CorrelationIdSeed: "corr-seed",
            Headers: new Dictionary<string, string> { ["trace"] = "trace-1" });

        var seed = command.Should().BeAssignableTo<ICommandContextSeed>().Subject;
        seed.CommandId.Should().Be("cmd-seed");
        seed.CorrelationId.Should().Be("corr-seed");
        seed.Headers.Should().Contain("trace", "trace-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldActivateBeforeInnerInteraction_AndPassSeededCommand()
    {
        var operations = new List<string>();
        var activation = new RecordingActivationPort(operations);
        var interaction = new RecordingInteractionService(operations)
        {
            ResultFactory = (command, _, _, _) => Task.FromResult(Success(command)),
        };
        var port = CreatePort(activation, interaction, operations);

        var result = await port.ExecuteAsync(
            Request(headers: new Dictionary<string, string> { ["trace"] = "trace-1" }),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        activation.Activations.Should().ContainSingle();
        interaction.Commands.Should().ContainSingle();
        var activated = activation.Activations[0];
        var command = interaction.Commands[0];
        command.PreferredActorId.Should().Be("draft-actor");
        command.CommandIdSeed.Should().Be(activated.CommandId);
        command.CorrelationIdSeed.Should().Be(activated.CorrelationId);
        command.Headers.Should().Contain("trace", "trace-1");
        operations.Should().ContainInOrder(
            "runtime:create:draft-actor",
            "registry:add:draft-actor",
            "activate:draft-actor",
            "interaction:draft-actor");
        activation.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnProjectionUnavailable_AndSkipInteraction_WhenActivationFails()
    {
        var activation = new RecordingActivationPort { FailActivation = true };
        var interaction = new RecordingInteractionService();
        var runtime = new RecordingActorRuntime(_ => null);
        var registry = new RecordingRegistryCommandPort();
        var port = CreatePort(activation, interaction, runtime: runtime, registry: registry);

        var result = await port.ExecuteAsync(
            Request(),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ProjectionUnavailable);
        interaction.Commands.Should().BeEmpty();
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        registry.UnregisteredActors.Should().ContainSingle().Which.ActorId.Should().Be("draft-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReleaseActivation_WhenInnerFailsBeforeAccepted()
    {
        var activation = new RecordingActivationPort();
        var runtime = new RecordingActorRuntime(_ => null);
        var registry = new RecordingRegistryCommandPort();
        var interaction = new RecordingInteractionService
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>
                    .Failure(GAgentDraftRunStartError.ActorTypeMismatch)),
        };
        var port = CreatePort(activation, interaction, runtime: runtime, registry: registry);

        var result = await port.ExecuteAsync(
            Request(),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        activation.Released.Should().ContainSingle().Which.Should().BeSameAs(activation.Handles.Single());
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        registry.UnregisteredActors.Should().ContainSingle().Which.ActorId.Should().Be("draft-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotReleaseActivation_WhenInnerAcceptedPathOwnsCleanup()
    {
        var activation = new RecordingActivationPort();
        var interaction = new RecordingInteractionService
        {
            ResultFactory = (command, _, _, _) => Task.FromResult(Success(command)),
        };
        var port = CreatePort(activation, interaction);

        var result = await port.ExecuteAsync(
            Request(),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        activation.Released.Should().BeEmpty();
    }

    private static GAgentDraftRunInteractionService CreatePort(
        RecordingActivationPort activation,
        RecordingInteractionService interaction,
        List<string>? operations = null,
        RecordingActorRuntime? runtime = null,
        RecordingRegistryCommandPort? registry = null) =>
        new(
            runtime ?? new RecordingActorRuntime(_ => null, operations),
            registry ?? new RecordingRegistryCommandPort(operations),
            new RecordingAdmissionPort(),
            interaction,
            activation);

    private static GAgentDraftRunInteractionRequest Request(
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(
            "scope-a",
            typeof(TestAgent).AssemblyQualifiedName!,
            "hello",
            "draft-actor",
            "session-1",
            Headers: headers);

    private static CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus> Success(
        GAgentDraftRunCommand command) =>
        CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Success(
            new GAgentDraftRunAcceptedReceipt(
                command.PreferredActorId ?? "actor-1",
                command.ActorTypeName,
                command.CommandIdSeed ?? "cmd-1",
                command.CorrelationIdSeed ?? "corr-1",
                command.SessionId ?? string.Empty),
            new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(
                GAgentDraftRunCompletionStatus.RunFinished,
                true));

    private sealed class RecordingActivationPort(List<string>? operations = null)
        : IGAgentDraftRunObservationScopeActivationPort
    {
        public List<(string ActorId, string CommandId, string CorrelationId)> Activations { get; } = [];
        public List<GAgentDraftRunObservationScopeActivation> Handles { get; } = [];
        public List<GAgentDraftRunObservationScopeActivation> Released { get; } = [];
        public bool FailActivation { get; init; }

        public Task<GAgentDraftRunObservationScopeActivation?> ActivateAsync(
            string actorId,
            string commandId,
            string correlationId,
            CancellationToken ct = default)
        {
            operations?.Add($"activate:{actorId}");
            Activations.Add((actorId, commandId, correlationId));
            if (FailActivation)
                return Task.FromResult<GAgentDraftRunObservationScopeActivation?>(null);

            var handle = new GAgentDraftRunObservationScopeActivation(actorId, commandId, correlationId);
            Handles.Add(handle);
            return Task.FromResult<GAgentDraftRunObservationScopeActivation?>(handle);
        }

        public Task ReleaseAsync(
            GAgentDraftRunObservationScopeActivation activation,
            CancellationToken ct = default)
        {
            Released.Add(activation);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingInteractionService(List<string>? operations = null)
        : ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus>
    {
        public List<GAgentDraftRunCommand> Commands { get; } = [];

        public Func<
            GAgentDraftRunCommand,
            Func<AGUIEvent, CancellationToken, ValueTask>,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>?,
            CancellationToken,
            Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>>>? ResultFactory { get; init; }

        public Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>> ExecuteAsync(
            GAgentDraftRunCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            operations?.Add($"interaction:{command.PreferredActorId}");
            Commands.Add(command);
            return ResultFactory?.Invoke(command, emitAsync, onAcceptedAsync, ct)
                ?? Task.FromResult(Success(command));
        }

        async Task<RealtimeSessionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>>
            IRealtimeSession<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus>.ExecuteAsync(
                GAgentDraftRunCommand inbound,
                Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
                Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                CancellationToken ct)
        {
            return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class RecordingRegistryCommandPort(List<string>? operations = null) : IGAgentActorRegistryCommandPort
    {
        public List<GAgentActorRegistration> RegisteredActors { get; } = [];
        public List<GAgentActorRegistration> UnregisteredActors { get; } = [];

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations?.Add($"registry:add:{registration.ActorId}");
            RegisteredActors.Add(registration);
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionVisible));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            UnregisteredActors.Add(registration);
            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }
    }

    private sealed class RecordingAdmissionPort : IScopeResourceAdmissionPort
    {
        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ScopeResourceAdmissionResult.Allowed());
    }

    private sealed class RecordingActorRuntime(
        Func<string, IActor?> getAsync,
        List<string>? operations = null) : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _createdActors = new(StringComparer.Ordinal);
        public List<string> DestroyedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? "created";
            operations?.Add($"runtime:create:{actorId}");
            var actor = new TestActor(actorId);
            _createdActors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedActorIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_createdActors.TryGetValue(id, out var actor) ? actor : getAsync(id));

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_createdActors.ContainsKey(id) || getAsync(id) is not null);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
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
