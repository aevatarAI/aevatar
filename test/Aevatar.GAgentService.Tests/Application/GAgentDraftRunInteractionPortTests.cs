using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Application.ScopeGAgents;
using Aevatar.AGUI.Contracts;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class GAgentDraftRunInteractionPortTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldReturnUnknownActorType_WhenTypeCannotBeResolved()
    {
        var port = CreatePort(
            new RecordingActorRuntime(_ => null),
            new RecordingRegistryCommandPort(),
            new RecordingAdmissionPort(),
            new RecordingInteractionService());

        var result = await port.ExecuteAsync(
            Request("Aevatar.IamNotReal, Aevatar.IamNotReal"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.UnknownActorType);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReuseExistingActor_WithoutRegisteringAgain()
    {
        var runtime = new RecordingActorRuntime(id => id == "existing-actor" ? new TestActor(id) : null);
        var registry = new RecordingRegistryCommandPort();
        var admission = new RecordingAdmissionPort { Result = ScopeResourceAdmissionResult.Allowed() };
        var interaction = new RecordingInteractionService
        {
            ResultFactory = (command, _, _, _) => Task.FromResult(Success(command)),
        };
        var port = CreatePort(runtime, registry, admission, interaction);

        var result = await port.ExecuteAsync(
            Request(preferredActorId: "existing-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        runtime.CreateCalls.Should().BeEmpty();
        registry.RegisteredActors.Should().BeEmpty();
        admission.Targets.Should().ContainSingle().Which.Should().Be(new ScopeResourceTarget(
            "scope-a",
            ScopeResourceKind.GAgentActor,
            typeof(TestAgent).AssemblyQualifiedName!,
            "existing-actor",
            ScopeResourceOperation.DraftRunReuse));
        interaction.Commands.Should().ContainSingle().Which.PreferredActorId.Should().Be("existing-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectExistingActor_WhenAdmissionRejectsReuse()
    {
        var runtime = new RecordingActorRuntime(id => id == "existing-actor" ? new TestActor(id) : null);
        var interaction = new RecordingInteractionService();
        var port = CreatePort(
            runtime,
            new RecordingRegistryCommandPort(),
            new RecordingAdmissionPort { Result = ScopeResourceAdmissionResult.ScopeMismatch() },
            interaction);

        var result = await port.ExecuteAsync(
            Request(preferredActorId: "existing-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ActorTypeMismatch);
        runtime.DestroyedActorIds.Should().BeEmpty();
        interaction.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRollbackCreatedActor_WhenRegistrationThrows()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(_ => null, operations);
        var registry = new RecordingRegistryCommandPort(operations)
        {
            ThrowOnRegister = new InvalidOperationException("registry unavailable"),
        };
        var port = CreatePort(runtime, registry, new RecordingAdmissionPort(), new RecordingInteractionService());

        var act = async () => await port.ExecuteAsync(
            Request(preferredActorId: "draft-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("registry unavailable");
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        registry.UnregisteredActors.Should().ContainSingle().Which.ActorId.Should().Be("draft-actor");
        operations.Should().ContainInOrder(
            "runtime:create:draft-actor",
            "registry:add:draft-actor",
            "registry:remove:draft-actor",
            "runtime:destroy:draft-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRollbackCreatedActor_WhenRegistrationIsNotAdmissionVisible()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(_ => null, operations);
        var registry = new RecordingRegistryCommandPort(operations)
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var interaction = new RecordingInteractionService();
        var port = CreatePort(runtime, registry, new RecordingAdmissionPort(), interaction);

        var result = await port.ExecuteAsync(
            Request(preferredActorId: "draft-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ActorTypeMismatch);
        interaction.Commands.Should().BeEmpty();
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        operations.Should().ContainInOrder(
            "runtime:create:draft-actor",
            "registry:add:draft-actor",
            "registry:remove:draft-actor",
            "runtime:destroy:draft-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRollbackCreatedActor_WhenObservationStartFails()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(_ => null, operations);
        var registry = new RecordingRegistryCommandPort(operations);
        var interaction = new RecordingInteractionService
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.ProjectionUnavailable)),
        };
        var port = CreatePort(runtime, registry, new RecordingAdmissionPort(), interaction);

        var result = await port.ExecuteAsync(
            Request(preferredActorId: "draft-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ProjectionUnavailable);
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        registry.UnregisteredActors.Should().ContainSingle().Which.ActorId.Should().Be("draft-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRollbackCreatedActor_WhenDispatchThrows()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(_ => null, operations);
        var registry = new RecordingRegistryCommandPort(operations);
        var interaction = new RecordingInteractionService
        {
            ResultFactory = (_, _, _, _) => throw new InvalidOperationException("dispatch failed"),
        };
        var port = CreatePort(runtime, registry, new RecordingAdmissionPort(), interaction);

        var act = async () => await port.ExecuteAsync(
            Request(preferredActorId: "draft-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed");
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        registry.UnregisteredActors.Should().ContainSingle().Which.ActorId.Should().Be("draft-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRollbackCreatedActor_WhenInteractionIsCanceled()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(_ => null, operations);
        var registry = new RecordingRegistryCommandPort(operations);
        var interaction = new RecordingInteractionService
        {
            ResultFactory = (_, _, _, _) => throw new OperationCanceledException("client disconnected"),
        };
        var port = CreatePort(runtime, registry, new RecordingAdmissionPort(), interaction);

        var act = async () => await port.ExecuteAsync(
            Request(preferredActorId: "draft-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        registry.UnregisteredActors.Should().ContainSingle().Which.ActorId.Should().Be("draft-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRollbackCreatedActor_WhenInteractionReturnsNonTerminalFailure()
    {
        var runtime = new RecordingActorRuntime(_ => null);
        var registry = new RecordingRegistryCommandPort();
        var interaction = new RecordingInteractionService
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.ActorTypeMismatch)),
        };
        var port = CreatePort(runtime, registry, new RecordingAdmissionPort(), interaction);

        var result = await port.ExecuteAsync(
            Request(preferredActorId: "draft-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ActorTypeMismatch);
        runtime.DestroyedActorIds.Should().ContainSingle("draft-actor");
        registry.UnregisteredActors.Should().ContainSingle().Which.ActorId.Should().Be("draft-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotRollbackCreatedActor_WhenDurableTerminalSuccessCompletes()
    {
        var runtime = new RecordingActorRuntime(_ => null);
        var registry = new RecordingRegistryCommandPort();
        var interaction = new RecordingInteractionService
        {
            ResultFactory = (command, _, _, _) => Task.FromResult(Success(command)),
        };
        var port = CreatePort(runtime, registry, new RecordingAdmissionPort(), interaction);

        var result = await port.ExecuteAsync(
            Request(preferredActorId: "draft-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        runtime.DestroyedActorIds.Should().BeEmpty();
        registry.UnregisteredActors.Should().BeEmpty();
        registry.RegisteredActors.Should().ContainSingle().Which.ActorId.Should().Be("draft-actor");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPreserveTypedToolControlFields()
    {
        var interaction = new RecordingInteractionService();
        var port = CreatePort(
            new RecordingActorRuntime(_ => null),
            new RecordingRegistryCommandPort(),
            new RecordingAdmissionPort(),
            interaction);

        var toolContext = NewToolContext();
        var llmControl = NewLlmControl();
        var result = await port.ExecuteAsync(
            Request(toolContext: toolContext, llmControl: llmControl),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        interaction.Commands.Should().ContainSingle();
        interaction.Commands[0].ToolContext.Should().BeEquivalentTo(toolContext);
        interaction.Commands[0].LlmControl.Should().BeEquivalentTo(llmControl);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotRollbackReusedActor_WhenInteractionFails()
    {
        var runtime = new RecordingActorRuntime(id => id == "existing-actor" ? new TestActor(id) : null);
        var registry = new RecordingRegistryCommandPort();
        var interaction = new RecordingInteractionService
        {
            ResultFactory = (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(
                    GAgentDraftRunStartError.ProjectionUnavailable)),
        };
        var port = CreatePort(
            runtime,
            registry,
            new RecordingAdmissionPort { Result = ScopeResourceAdmissionResult.Allowed() },
            interaction);

        var result = await port.ExecuteAsync(
            Request(preferredActorId: "existing-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        runtime.DestroyedActorIds.Should().BeEmpty();
        registry.UnregisteredActors.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotReleaseActivationOrRollback_WhenInnerAcceptsThenReturnsFailure()
    {
        var activation = new RecordingActivationPort();
        var runtime = new RecordingActorRuntime(_ => null);
        var registry = new RecordingRegistryCommandPort();
        var interaction = new RecordingInteractionService
        {
            ResultFactory = async (command, _, onAcceptedAsync, ct) =>
            {
                onAcceptedAsync.Should().NotBeNull();
                await onAcceptedAsync!(
                    new GAgentDraftRunAcceptedReceipt(
                        command.PreferredActorId ?? "actor-1",
                        command.ActorTypeName,
                        command.CommandIdSeed ?? "cmd-1",
                        command.CorrelationIdSeed ?? "corr-1",
                        command.SessionId ?? string.Empty),
                    ct);

                return CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>
                    .Failure(GAgentDraftRunStartError.ProjectionUnavailable);
            },
        };
        var port = CreatePort(runtime, registry, new RecordingAdmissionPort(), interaction, activation);

        var result = await port.ExecuteAsync(
            Request(preferredActorId: "draft-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(GAgentDraftRunStartError.ProjectionUnavailable);
        activation.Released.Should().BeEmpty();
        runtime.DestroyedActorIds.Should().BeEmpty();
        registry.UnregisteredActors.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotReleaseActivationOrRollback_WhenInnerAcceptsThenThrows()
    {
        var activation = new RecordingActivationPort();
        var runtime = new RecordingActorRuntime(_ => null);
        var registry = new RecordingRegistryCommandPort();
        var interaction = new RecordingInteractionService
        {
            ResultFactory = async (command, _, onAcceptedAsync, ct) =>
            {
                onAcceptedAsync.Should().NotBeNull();
                await onAcceptedAsync!(
                    new GAgentDraftRunAcceptedReceipt(
                        command.PreferredActorId ?? "actor-1",
                        command.ActorTypeName,
                        command.CommandIdSeed ?? "cmd-1",
                        command.CorrelationIdSeed ?? "corr-1",
                        command.SessionId ?? string.Empty),
                    ct);

                throw new InvalidOperationException("pump failed");
            },
        };
        var port = CreatePort(runtime, registry, new RecordingAdmissionPort(), interaction, activation);

        var act = () => port.ExecuteAsync(
            Request(preferredActorId: "draft-actor"),
            (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("pump failed");
        activation.Released.Should().BeEmpty();
        runtime.DestroyedActorIds.Should().BeEmpty();
        registry.UnregisteredActors.Should().BeEmpty();
    }

    private static GAgentDraftRunInteractionService CreatePort(
        RecordingActorRuntime runtime,
        RecordingRegistryCommandPort registry,
        RecordingAdmissionPort admission,
        RecordingInteractionService interaction,
        IGAgentDraftRunObservationScopeActivationPort? activation = null) =>
        new(runtime, registry, admission, interaction, activation ?? new NoOpActivationPort());

    private static GAgentDraftRunInteractionRequest Request(
        string? actorTypeName = null,
        string? preferredActorId = "draft-actor",
        AgentToolExecutionContext? toolContext = null,
        LLMControlContext? llmControl = null) =>
        new(
            "scope-a",
            actorTypeName ?? typeof(TestAgent).AssemblyQualifiedName!,
            "hello",
            preferredActorId,
            "session-1",
            " token ",
            " model ",
            " route ",
            ToolContext: toolContext,
            LlmControl: llmControl);

    private static AgentToolExecutionContext NewToolContext() =>
        new(
            new AgentToolRequestIdentity("request-1", "call-1"),
            new AgentToolCredentials("access-token", "org-token", "sender-token"),
            new AgentToolCallerContext("scope-a", "owner-a", "response-1"),
            new AgentToolChannelContext("telegram", "sender-1", "registration-scope-1", "message-1", "platform-message-1"),
            new AgentToolSenderBindingContext("binding-1"),
            new LLMRequestRoutingContext("model-1", "route-1", 3, "remember"),
            new AgentToolConnectedServicesContext("connected"),
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["external"] = "value",
            });

    private static LLMControlContext NewLlmControl() =>
        new("access-token", "org-token", "sender-token", "model-1", "route-1", 3, "remember");

    private static CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus> Success(
        GAgentDraftRunCommand command) =>
        CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Success(
            new GAgentDraftRunAcceptedReceipt(
                command.PreferredActorId ?? "actor-1",
                command.ActorTypeName,
                "cmd-1",
                "corr-1",
                command.SessionId ?? string.Empty),
            new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(
                GAgentDraftRunCompletionStatus.TextMessageCompleted,
                true));

    private sealed class RecordingInteractionService
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

    private sealed class NoOpActivationPort : IGAgentDraftRunObservationScopeActivationPort
    {
        public Task<GAgentDraftRunObservationScopeActivation?> ActivateAsync(
            string actorId,
            string commandId,
            string correlationId,
            CancellationToken ct = default) =>
            Task.FromResult<GAgentDraftRunObservationScopeActivation?>(new GAgentDraftRunObservationScopeActivation(
                actorId,
                commandId,
                correlationId));

        public Task ReleaseAsync(
            GAgentDraftRunObservationScopeActivation activation,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingActivationPort : IGAgentDraftRunObservationScopeActivationPort
    {
        public List<GAgentDraftRunObservationScopeActivation> Handles { get; } = [];
        public List<GAgentDraftRunObservationScopeActivation> Released { get; } = [];

        public Task<GAgentDraftRunObservationScopeActivation?> ActivateAsync(
            string actorId,
            string commandId,
            string correlationId,
            CancellationToken ct = default)
        {
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

    private sealed class RecordingRegistryCommandPort(List<string>? operations = null) : IGAgentActorRegistryCommandPort
    {
        public List<GAgentActorRegistration> RegisteredActors { get; } = [];
        public List<GAgentActorRegistration> UnregisteredActors { get; } = [];
        public Exception? ThrowOnRegister { get; init; }
        public Exception? ThrowOnUnregister { get; init; }
        public GAgentActorRegistryCommandStage RegisterStage { get; init; } =
            GAgentActorRegistryCommandStage.AdmissionVisible;

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations?.Add($"registry:add:{registration.ActorId}");
            RegisteredActors.Add(registration);
            if (ThrowOnRegister is not null)
                throw ThrowOnRegister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(registration, RegisterStage));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations?.Add($"registry:remove:{registration.ActorId}");
            UnregisteredActors.Add(registration);
            if (ThrowOnUnregister is not null)
                throw ThrowOnUnregister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }
    }

    private sealed class RecordingAdmissionPort : IScopeResourceAdmissionPort
    {
        public ScopeResourceAdmissionResult Result { get; init; } = ScopeResourceAdmissionResult.NotFound();
        public List<ScopeResourceTarget> Targets { get; } = [];

        public Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
            ScopeResourceTarget target,
            CancellationToken cancellationToken = default)
        {
            Targets.Add(target);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingActorRuntime(
        Func<string, IActor?> getAsync,
        List<string>? operations = null) : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _createdActors = new(StringComparer.Ordinal);
        public List<(Type AgentType, string? ActorId)> CreateCalls { get; } = [];
        public List<string> DestroyedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? "created";
            operations?.Add($"runtime:create:{actorId}");
            CreateCalls.Add((agentType, actorId));
            var actor = new TestActor(actorId);
            _createdActors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            operations?.Add($"runtime:destroy:{id}");
            DestroyedActorIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            if (_createdActors.TryGetValue(id, out var actor))
                return Task.FromResult<IActor?>(actor);

            return Task.FromResult(getAsync(id));
        }

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
