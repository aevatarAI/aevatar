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
using Aevatar.Presentation.AGUI;
using Aevatar.Scripting.Core.Ports;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

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
        result.FinalizeResult!.Completed.Should().BeTrue();
        runtimePort.Invocations.Should().ContainSingle(invocation =>
            invocation.RuntimeActorId == "runtime-1" &&
            invocation.RunId == "run-1" &&
            invocation.CommandId == "cmd-1" &&
            invocation.CorrelationId == "corr-1");
        projectionPort.EnsureCalls.Should().ContainSingle(call =>
            call.ActorId == "runtime-1" && call.RunId == "run-1");
        projectionPort.ReleaseCalls.Should().ContainSingle(call =>
            call.ActorId == "runtime-1" && call.RunId == "run-1");
        emitted.Should().ContainSingle(evt => evt.EventCase == AGUIEvent.EventOneofCase.RunFinished);
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

    private static ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus> CreateInteraction(
        RecordingScriptServiceAguiProjectionPort projectionPort,
        RecordingScriptRuntimeCommandPort runtimePort)
    {
        var pipeline = new DefaultCommandDispatchPipeline<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>(
            new ScriptServiceRunCommandTargetResolver(projectionPort),
            new DefaultCommandContextPolicy(),
            new ScriptServiceRunCommandTargetBinder(projectionPort),
            new ScriptServiceRunEnvelopeFactory(),
            new ScriptServiceRunCommandDispatcher(runtimePort),
            new ScriptServiceRunAcceptedReceiptFactory());

        return new DefaultCommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, AGUIEvent, ScriptServiceRunCompletionStatus>(
            pipeline,
            new DefaultEventOutputStream<AGUIEvent, AGUIEvent>(new IdentityEventFrameMapper<AGUIEvent>()),
            new ScriptServiceRunCompletionPolicy(),
            new ScriptServiceRunFinalizeEmitter(),
            new ScriptServiceRunDurableCompletionResolver());
    }

    private static ScriptServiceRunCommand CreateCommand() =>
        new(
            ScopeId: "scope-a",
            ServiceId: "svc-a",
            ServiceKey: "scope-a:default:default:svc-a",
            EndpointId: "chat",
            RevisionId: "rev-1",
            DeploymentId: "dep-1",
            RuntimeActorId: "runtime-1",
            DefinitionActorId: "definition-1",
            ScriptRevision: "script-rev-1",
            Prompt: "hello",
            SessionId: "session-1",
            RunId: "run-1",
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
        public List<(string ActorId, string RunId)> EnsureCalls { get; } = [];
        public List<(string ActorId, string RunId)> ReleaseCalls { get; } = [];
        public bool ProjectionEnabled => true;

        public Task<IScriptServiceAguiProjectionLease?> EnsureRunProjectionAsync(
            string actorId,
            string runId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureCalls.Add((actorId, runId));
            return Task.FromResult<IScriptServiceAguiProjectionLease?>(new Lease(actorId, runId));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IScriptServiceAguiProjectionLease lease,
            IEventSink<AGUIEvent> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            _ = Task.Run(async () =>
            {
                foreach (var message in Messages)
                {
                    try
                    {
                        await sink.PushAsync(message, CancellationToken.None);
                    }
                    catch (EventSinkCompletedException)
                    {
                        break;
                    }
                }
            }, CancellationToken.None);
            return Task.FromResult<IAsyncDisposable?>(null);
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

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct) =>
            throw new NotSupportedException();

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
                scopeId));
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
        string? ScopeId);

    private sealed class RecordingScriptServiceRunInteraction
        : ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>
    {
        public Task<CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>> ExecuteAsync(
            ScriptServiceRunCommand command,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            var receipt = new ScriptServiceRunAcceptedReceipt(
                command.RuntimeActorId,
                command.RunId,
                command.CommandId,
                command.CorrelationId);
            return ExecuteAsync(receipt, onAcceptedAsync, ct);
        }

        private static async Task<CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>> ExecuteAsync(
            ScriptServiceRunAcceptedReceipt receipt,
            Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
            CancellationToken ct)
        {
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            return CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>.Success(
                receipt,
                new CommandInteractionFinalizeResult<ScriptServiceRunCompletionStatus>(
                    ScriptServiceRunCompletionStatus.Incomplete,
                    false));
        }
    }

    private sealed class RecordingServiceRunRegistrationPort : IServiceRunRegistrationPort
    {
        public List<ServiceRunRecord> Registered { get; } = [];

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
            Task.CompletedTask;
    }
}
