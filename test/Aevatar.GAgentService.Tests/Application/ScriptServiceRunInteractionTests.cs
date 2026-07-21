using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Application.Scripts;
using Aevatar.AGUI.Contracts;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using ChatRequestEvent = Aevatar.AI.Abstractions.ChatRequestEvent;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScriptServiceRunInteractionTests
{
    [Fact]
    public async Task Interaction_ShouldAttachProjectionDispatchRuntimeAndCleanup()
    {
        var projectionPort = new RecordingScriptServiceAguiProjectionPort
        {
            Messages =
            {
                new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent { ThreadId = "runtime-1", RunId = "run-1" },
                },
            },
        };
        var runtimePort = new RecordingScriptRuntimeCommandPort();
        var interaction = CreateInteraction(projectionPort, runtimePort);
        var emitted = new List<AGUIEvent>();

        var result = await interaction.ExecuteAsync(
            CreateCommand(),
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt!.RunId.Should().Be("run-1");
        result.Receipt.CommandId.Should().Be("cmd-1");
        result.Receipt.CorrelationId.Should().Be("corr-1");
        result.FinalizeResult!.Completed.Should().BeTrue();
        var invocation = runtimePort.Invocations.Should().ContainSingle().Subject;
        invocation.RuntimeActorId.Should().Be("runtime-1");
        invocation.RunId.Should().Be("run-1");
        invocation.CommandId.Should().Be("cmd-1");
        invocation.CorrelationId.Should().Be("corr-1");
        invocation.ScriptRevision.Should().Be("script-rev-1");
        invocation.DefinitionActorId.Should().Be("definition-1");
        invocation.ScopeId.Should().Be("scope-a");
        invocation.InputPayload.Should().NotBeNull();
        invocation.RequestedEventType.Should().Be(invocation.InputPayload!.TypeUrl);
        var runtimeRequest = invocation.InputPayload.Unpack<ChatRequestEvent>();
        runtimeRequest.Prompt.Should().Be("hello");
        runtimeRequest.SessionId.Should().Be("session-1");
        runtimeRequest.ScopeId.Should().Be("scope-a");
        runtimeRequest.Metadata.Should().ContainKey("trace-id")
            .WhoseValue.Should().Be("trace-1");
        projectionPort.ReleaseCalls.Should().ContainSingle(call =>
            call.ActorId == "runtime-1" && call.RunId == "run-1");
        emitted.Should().ContainSingle(evt => evt.EventCase == AGUIEvent.EventOneofCase.RunFinished);
    }

    [Fact]
    public async Task Interaction_ShouldFailWithRuntimeActorUnavailable_WhenRuntimeActorIdMissing()
    {
        var projectionPort = new RecordingScriptServiceAguiProjectionPort();
        var runtimePort = new RecordingScriptRuntimeCommandPort();
        var interaction = CreateInteraction(projectionPort, runtimePort);

        var result = await interaction.ExecuteAsync(
            CreateCommand(runtimeActorId: " "),
            (_, _) => ValueTask.CompletedTask,
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(ScriptServiceRunStartErrorCode.RuntimeActorUnavailable);
        result.Error.FieldName.Should().Be("runtimeActorId");
        runtimePort.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Interaction_ShouldFailWithInvalidArgument_WhenRunIdMissing()
    {
        var projectionPort = new RecordingScriptServiceAguiProjectionPort();
        var runtimePort = new RecordingScriptRuntimeCommandPort();
        var interaction = CreateInteraction(projectionPort, runtimePort);

        var result = await interaction.ExecuteAsync(
            CreateCommand(runId: " "),
            (_, _) => ValueTask.CompletedTask,
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(ScriptServiceRunStartErrorCode.InvalidArgument);
        result.Error.FieldName.Should().Be("runId");
        runtimePort.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Interaction_ShouldFailWithProjectionUnavailable_AndNotDispatchRuntime_WhenProjectionAttachFails()
    {
        var projectionPort = new RecordingScriptServiceAguiProjectionPort { ReturnNullLease = true };
        var runtimePort = new RecordingScriptRuntimeCommandPort();
        var interaction = CreateInteraction(projectionPort, runtimePort);

        var result = await interaction.ExecuteAsync(
            CreateCommand(),
            (_, _) => ValueTask.CompletedTask,
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(ScriptServiceRunStartErrorCode.ProjectionUnavailable);
        projectionPort.AttachCalls.Should().BeEmpty();
        projectionPort.ReleaseCalls.Should().BeEmpty();
        runtimePort.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Interaction_ShouldReleaseProjection_WhenRuntimeDispatchFailsAfterAttach()
    {
        var projectionPort = new RecordingScriptServiceAguiProjectionPort();
        var runtimePort = new RecordingScriptRuntimeCommandPort
        {
            DispatchException = new InvalidOperationException("runtime dispatch failed"),
        };
        var interaction = CreateInteraction(projectionPort, runtimePort);

        var act = () => interaction.ExecuteAsync(
            CreateCommand(),
            (_, _) => ValueTask.CompletedTask,
            null,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("runtime dispatch failed");
        runtimePort.Invocations.Should().ContainSingle(invocation =>
            invocation.RuntimeActorId == "runtime-1" &&
            invocation.RunId == "run-1");
        projectionPort.AttachCalls.Should().ContainSingle(call =>
            call.ActorId == "runtime-1" && call.RunId == "run-1");
        projectionPort.ReleaseCalls.Should().ContainSingle(call =>
            call.ActorId == "runtime-1" && call.RunId == "run-1");
    }

    [Fact]
    public async Task Interaction_ShouldCompleteWithRunError_WhenRunErrorArrives()
    {
        var projectionPort = new RecordingScriptServiceAguiProjectionPort
        {
            Messages =
            {
                new AGUIEvent
                {
                    RunError = new RunErrorEvent { Message = "failed" },
                },
            },
        };
        var runtimePort = new RecordingScriptRuntimeCommandPort();
        var interaction = CreateInteraction(projectionPort, runtimePort);
        var emitted = new List<AGUIEvent>();

        var result = await interaction.ExecuteAsync(
            CreateCommand(),
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult!.Completed.Should().BeTrue();
        result.FinalizeResult.Completion.Should().Be(ScriptServiceRunCompletionStatus.RunError);
        emitted.Should().ContainSingle(evt => evt.EventCase == AGUIEvent.EventOneofCase.RunError);
        runtimePort.Invocations.Should().ContainSingle();
        projectionPort.ReleaseCalls.Should().ContainSingle(call =>
            call.ActorId == "runtime-1" && call.RunId == "run-1");
    }

    [Fact]
    public async Task Interaction_ShouldEmitSyntheticRunError_WhenStreamEndsWithoutTerminalEvent()
    {
        var projectionPort = new RecordingScriptServiceAguiProjectionPort
        {
            CompleteAfterMessages = true,
            Messages =
            {
                new AGUIEvent
                {
                    TextMessageContent = new TextMessageContentEvent
                    {
                        MessageId = "msg-1",
                        Delta = "partial",
                    },
                },
            },
        };
        var runtimePort = new RecordingScriptRuntimeCommandPort();
        var interaction = CreateInteraction(projectionPort, runtimePort);
        var emitted = new List<AGUIEvent>();

        var result = await interaction.ExecuteAsync(
            CreateCommand(),
            (evt, _) =>
            {
                emitted.Add(evt);
                return ValueTask.CompletedTask;
            },
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult!.Completed.Should().BeFalse();
        result.FinalizeResult.Completion.Should().Be(ScriptServiceRunCompletionStatus.Incomplete);
        emitted.Should().Contain(evt => evt.EventCase == AGUIEvent.EventOneofCase.TextMessageContent);
        emitted.Should().ContainSingle(evt =>
            evt.EventCase == AGUIEvent.EventOneofCase.RunError &&
            evt.RunError.Message.Contains("ended before a terminal event", StringComparison.Ordinal));
        runtimePort.Invocations.Should().ContainSingle();
        projectionPort.ReleaseCalls.Should().ContainSingle(call =>
            call.ActorId == "runtime-1" && call.RunId == "run-1");
    }

    [Fact]
    public async Task RegistrationDecorator_ShouldRegisterServiceRunBeforeAcceptedCallback()
    {
        var inner = new RecordingScriptServiceRunInteraction();
        var order = new List<string>();
        var registrationPort = new RecordingServiceRunRegistrationPort
        {
            OnRegister = () => order.Add("registered"),
        };
        var decorator = new ScriptServiceRunRegistrationInteraction(inner, registrationPort);

        var result = await decorator.ExecuteAsync(
            CreateCommand(),
            (_, _) => ValueTask.CompletedTask,
            (_, _) =>
            {
                order.Add("accepted");
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        registrationPort.Registered.Should().ContainSingle();
        registrationPort.Registered[0].RunId.Should().Be("run-1");
        registrationPort.Registered[0].CommandId.Should().Be("cmd-1");
        registrationPort.Registered[0].CorrelationId.Should().Be("corr-1");
        registrationPort.Registered[0].ImplementationKind.Should().Be(ServiceImplementationKind.Scripting);
        order.Should().Equal("registered", "accepted");
    }

    [Fact]
    public async Task RegistrationDecorator_ShouldPersistCompletedTerminalOutput()
    {
        var inner = new RecordingScriptServiceRunInteraction
        {
            Frames =
            [
                new AGUIEvent
                {
                    TextMessageContent = new TextMessageContentEvent
                    {
                        MessageId = "msg-1",
                        Delta = "script output",
                    },
                },
                new AGUIEvent
                {
                    TextMessageEnd = new TextMessageEndEvent
                    {
                        MessageId = "msg-1",
                    },
                },
                new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent(),
                },
            ],
            Completion = ScriptServiceRunCompletionStatus.RunFinished,
            Completed = true,
        };
        var registrationPort = new RecordingServiceRunRegistrationPort();
        var decorator = new ScriptServiceRunRegistrationInteraction(inner, registrationPort);

        var result = await decorator.ExecuteAsync(
            CreateCommand(),
            (_, _) => ValueTask.CompletedTask,
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        registrationPort.StatusUpdates.Should().ContainSingle()
            .Which.Should().Be(("service-run:run-1", "run-1", ServiceRunStatus.Completed, "script output", string.Empty));
    }

    [Fact]
    public async Task RegistrationDecorator_ShouldPersistFailedTerminalError()
    {
        var inner = new RecordingScriptServiceRunInteraction
        {
            Frames =
            [
                new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = "script failed",
                    },
                },
            ],
            Completion = ScriptServiceRunCompletionStatus.RunError,
            Completed = true,
        };
        var registrationPort = new RecordingServiceRunRegistrationPort();
        var decorator = new ScriptServiceRunRegistrationInteraction(inner, registrationPort);

        var result = await decorator.ExecuteAsync(
            CreateCommand(),
            (_, _) => ValueTask.CompletedTask,
            null,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        registrationPort.StatusUpdates.Should().ContainSingle()
            .Which.Should().Be(("service-run:run-1", "run-1", ServiceRunStatus.Failed, string.Empty, "script failed"));
    }

    [Fact]
    public async Task AddScriptServiceRunInteraction_ShouldResolveDecoratedInteraction_AndExecute()
    {
        var projectionPort = new RecordingScriptServiceAguiProjectionPort
        {
            Messages =
            {
                new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent { ThreadId = "runtime-1", RunId = "run-1" },
                },
            },
        };
        var runtimePort = new RecordingScriptRuntimeCommandPort();
        var registrationPort = new RecordingServiceRunRegistrationPort();
        await using var provider = new ServiceCollection()
            .AddSingleton<IScriptServiceAguiProjectionPort>(projectionPort)
            .AddSingleton<IScriptRuntimeCommandPort>(runtimePort)
            .AddSingleton<IServiceRunRegistrationPort>(registrationPort)
            .AddScriptServiceRunInteraction()
            .BuildServiceProvider();
        var interaction = provider.GetRequiredService<ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>>();
        var accepted = new List<ScriptServiceRunAcceptedReceipt>();

        var result = await interaction.ExecuteAsync(
            CreateCommand(),
            (_, _) => ValueTask.CompletedTask,
            (receipt, _) =>
            {
                accepted.Add(receipt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        runtimePort.Invocations.Should().ContainSingle(invocation =>
            invocation.RuntimeActorId == "runtime-1" &&
            invocation.RunId == "run-1" &&
            invocation.CommandId == "cmd-1" &&
            invocation.CorrelationId == "corr-1");
        registrationPort.Registered.Should().ContainSingle(record =>
            record.RunId == "run-1" &&
            record.CommandId == "cmd-1" &&
            record.CorrelationId == "corr-1" &&
            record.ImplementationKind == ServiceImplementationKind.Scripting);
        accepted.Should().ContainSingle(receipt =>
            receipt.RunId == "run-1" &&
            receipt.CommandId == "cmd-1" &&
            receipt.CorrelationId == "corr-1");
    }

    private static ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus> CreateInteraction(
        RecordingScriptServiceAguiProjectionPort projectionPort,
        RecordingScriptRuntimeCommandPort runtimePort)
    {
        var pipeline = new DefaultCommandDispatchPipeline<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>(
            new ScriptServiceRunCommandTargetResolver(projectionPort),
            new DefaultCommandContextPolicy(),
            new ScriptServiceRunEnvelopeFactory(),
            new ScriptServiceRunCommandDispatcher(runtimePort),
            new ScriptServiceRunAcceptedReceiptFactory());

        return new DefaultCommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, AGUIEvent, ScriptServiceRunCompletionStatus>(
            pipeline,
            new DefaultEventOutputStream<AGUIEvent, AGUIEvent>(new IdentityEventFrameMapper<AGUIEvent>()),
            new ScriptServiceRunCompletionPolicy(),
            new ScriptServiceRunFinalizeEmitter(),
            new ScriptServiceRunDurableCompletionResolver(),
            observationLifecycle: new ScriptServiceRunCommandTargetBinder(projectionPort));
    }

    private static ScriptServiceRunCommand CreateCommand(
        string runtimeActorId = "runtime-1",
        string runId = "run-1") =>
        new(
            ScopeId: "scope-a",
            ServiceId: "svc-a",
            ServiceKey: "scope-a:default:default:svc-a",
            EndpointId: "chat",
            RevisionId: "rev-1",
            DeploymentId: "dep-1",
            RuntimeActorId: runtimeActorId,
            DefinitionActorId: "definition-1",
            ScriptRevision: "script-rev-1",
            Prompt: "hello",
            SessionId: "session-1",
            RunId: runId,
            CommandId: "cmd-1",
            CorrelationId: "corr-1",
            Headers: new Dictionary<string, string> { ["trace-id"] = "trace-1" },
            Identity: new ServiceIdentity
            {
                TenantId = "scope-a",
                AppId = "default",
                Namespace = "default",
                ServiceId = "svc-a",
            });

    private sealed class RecordingScriptServiceAguiProjectionPort : IScriptServiceAguiProjectionPort
    {
        public List<AGUIEvent> Messages { get; } = [];
        public List<(string ActorId, string RunId)> AttachCalls { get; } = [];
        public List<(string ActorId, string RunId)> ReleaseCalls { get; } = [];
        public bool ReturnNullLease { get; init; }
        public bool CompleteAfterMessages { get; init; }
        public bool ProjectionEnabled => true;

        public async Task<EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>?> AttachExistingRunProjectionAsync(
            string actorId,
            string runId,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            if (ReturnNullLease)
                return null;

            var lease = new Lease(actorId, runId);
            var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct);
            return new EventSinkProjectionAttachment<IScriptServiceAguiProjectionLease>(lease, liveSinkLease);
        }

        public async Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IScriptServiceAguiProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AttachCalls.Add((lease.ActorId, lease.RunId));

            foreach (var message in Messages)
            {
                try
                {
                    await sink.PushAsync(message, ct);
                }
                catch (EventSinkCompletedException)
                {
                    break;
                }
            }

            if (CompleteAfterMessages)
                sink.Complete();

            return null;
        }

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default)
        {
            _ = liveSinkLease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(IScriptServiceAguiProjectionLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReleaseCalls.Add((lease.ActorId, lease.RunId));
            return Task.CompletedTask;
        }
    }

    private sealed record Lease(string ActorId, string RunId) : IScriptServiceAguiProjectionLease;

    private sealed class RecordingScriptRuntimeCommandPort : IScriptRuntimeCommandPort
    {
        public List<RuntimeInvocation> Invocations { get; } = [];

        public Exception? DispatchException { get; init; }

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            string commandId,
            string correlationId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            string? scopeId,
            string? completionNotificationActorId,
            string? completionNotificationDeliveryId,
            long completionNotificationExpiresAtUnixMs,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Invocations.Add(new RuntimeInvocation(
                runtimeActorId,
                runId,
                commandId,
                correlationId,
                inputPayload?.Clone(),
                scriptRevision,
                definitionActorId,
                requestedEventType,
                scopeId,
                completionNotificationDeliveryId,
                completionNotificationExpiresAtUnixMs));
            if (DispatchException != null)
                throw DispatchException;

            return Task.CompletedTask;
        }
    }

    private sealed record RuntimeInvocation(
        string RuntimeActorId,
        string RunId,
        string CommandId,
        string CorrelationId,
        Any? InputPayload,
        string ScriptRevision,
        string DefinitionActorId,
        string RequestedEventType,
        string? ScopeId,
        string? CompletionNotificationDeliveryId,
        long CompletionNotificationExpiresAtUnixMs);

    private sealed class RecordingScriptServiceRunInteraction
        : ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>
    {
        public IReadOnlyList<AGUIEvent> Frames { get; init; } = [];

        public ScriptServiceRunCompletionStatus Completion { get; init; } = ScriptServiceRunCompletionStatus.Incomplete;

        public bool Completed { get; init; }

        public async Task<CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>> ExecuteAsync(
            ScriptServiceRunCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var receipt = new ScriptServiceRunAcceptedReceipt(
                command.RuntimeActorId,
                command.RunId,
                command.CommandId,
                command.CorrelationId);
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);
            foreach (var frame in Frames)
                await emitAsync(frame, ct);

            return CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>.Success(
                receipt,
                new CommandInteractionFinalizeResult<ScriptServiceRunCompletionStatus>(
                    Completion,
                    Completed));
        }

        async Task<RealtimeSessionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>>
            IRealtimeSession<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>.ExecuteAsync(
                ScriptServiceRunCommand inbound,
                Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
                Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
                CancellationToken ct)
        {
            return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class RecordingServiceRunRegistrationPort : IServiceRunRegistrationPort
    {
        public List<ServiceRunRecord> Registered { get; } = [];
        public List<(string RunActorId, string RunId, ServiceRunStatus Status, string LastOutput, string LastError)> StatusUpdates { get; } = [];

        public Action? OnRegister { get; init; }

        public Task<ServiceRunRegistrationResult> RegisterAsync(ServiceRunRecord record, CancellationToken ct = default) =>
            RegisterCoreAsync(record, ct);

        public Task<ServiceRunRegistrationResult> RegisterCoreAsync(ServiceRunRecord record, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            OnRegister?.Invoke();
            Registered.Add(record.Clone());
            return Task.FromResult(new ServiceRunRegistrationResult($"service-run:{record.RunId}", record.RunId));
        }

        public Task UpdateStatusAsync(string runActorId, string runId, ServiceRunStatus status, CancellationToken ct = default) =>
            UpdateStatusAsync(runActorId, runId, status, null, null, ct);

        public Task UpdateStatusAsync(
            string runActorId,
            string runId,
            ServiceRunStatus status,
            string? lastOutput,
            string? lastError,
            CancellationToken ct = default)
        {
            StatusUpdates.Add((runActorId, runId, status, lastOutput ?? string.Empty, lastError ?? string.Empty));
            return Task.CompletedTask;
        }
    }
}
