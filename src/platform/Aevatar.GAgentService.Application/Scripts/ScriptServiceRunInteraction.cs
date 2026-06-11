using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.AGUI.Contracts;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Scripts;

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream inline orchestration in endpoints
//   New principle: use existing ICommandInteractionService skeleton with ScriptServiceRunCommand and Application-owned service-run registration decorator
internal sealed class ScriptServiceRunCommandTarget
    : ICommandDispatchTarget,
      ICommandEventTarget<AGUIEvent>,
      ICommandInteractionCleanupTarget<ScriptServiceRunAcceptedReceipt, ScriptServiceRunCompletionStatus>,
      ICommandDispatchCleanupAware
{
    private readonly IScriptServiceAguiProjectionPort _projectionPort;

    public ScriptServiceRunCommandTarget(
        string actorId,
        string runId,
        IScriptServiceAguiProjectionPort projectionPort)
    {
        ActorId = string.IsNullOrWhiteSpace(actorId)
            ? throw new ArgumentException("Runtime actor id is required.", nameof(actorId))
            : actorId.Trim();
        RunId = string.IsNullOrWhiteSpace(runId)
            ? throw new ArgumentException("Run id is required.", nameof(runId))
            : runId;
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public string ActorId { get; }

    public string TargetId => ActorId;

    public Any? InputPayload { get; private set; }

    public string RunId { get; private set; } = string.Empty;

    public string CommandId { get; private set; } = string.Empty;

    public string CorrelationId { get; private set; } = string.Empty;

    public string ScriptRevision { get; private set; } = string.Empty;

    public string DefinitionActorId { get; private set; } = string.Empty;

    public string ScopeId { get; private set; } = string.Empty;

    public IScriptServiceAguiProjectionLease? ProjectionLease { get; private set; }

    public IAsyncDisposable? LiveSinkLease { get; private set; }

    public IEventSink<AGUIEvent>? LiveSink { get; private set; }

    // Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
    //   Old pattern: endpoint-local variables carried script runtime payload and dispatch identities
    //   New principle: bind runtime payload and separated run/command/correlation identities onto the command target
    public void BindRunInput(
        Any inputPayload,
        ScriptServiceRunCommand command,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        InputPayload = inputPayload?.Clone() ?? throw new ArgumentNullException(nameof(inputPayload));
        RunId = command.RunId ?? string.Empty;
        CommandId = context.CommandId;
        CorrelationId = context.CorrelationId;
        ScriptRevision = command.ScriptRevision ?? string.Empty;
        DefinitionActorId = command.DefinitionActorId ?? string.Empty;
        ScopeId = command.ScopeId ?? string.Empty;
    }

    public void BindLiveObservation(
        IScriptServiceAguiProjectionLease projectionLease,
        IAsyncDisposable? liveSinkLease,
        IEventSink<AGUIEvent> liveSink)
    {
        ProjectionLease = projectionLease ?? throw new ArgumentNullException(nameof(projectionLease));
        LiveSinkLease = liveSinkLease;
        LiveSink = liveSink ?? throw new ArgumentNullException(nameof(liveSink));
    }

    public IEventSink<AGUIEvent> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("Script service run live sink is not bound.");

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(ct);

    public Task ReleaseAfterInteractionAsync(
        ScriptServiceRunAcceptedReceipt receipt,
        CommandInteractionCleanupContext<ScriptServiceRunCompletionStatus> cleanup,
        CancellationToken ct = default)
    {
        _ = receipt;
        _ = cleanup;
        return ReleaseAsync(ct);
    }

    private async Task ReleaseAsync(CancellationToken ct)
    {
        Exception? firstException = null;
        var projectionLease = ProjectionLease;
        var liveSink = LiveSink;

        if (projectionLease != null && liveSink != null)
        {
            try
            {
                await _projectionPort.DetachReleaseAndDisposeAsync(
                    projectionLease,
                    LiveSinkLease,
                    liveSink,
                    null,
                    ct);
                ProjectionLease = null;
                LiveSinkLease = null;
                LiveSink = null;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }
        else
        {
            if (liveSink != null)
            {
                try
                {
                    liveSink.Complete();
                    await liveSink.DisposeAsync();
                    LiveSinkLease = null;
                    LiveSink = null;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
            }

            if (projectionLease != null)
            {
                try
                {
                    await _projectionPort.ReleaseActorProjectionAsync(projectionLease, ct);
                    ProjectionLease = null;
                    LiveSinkLease = null;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
            }
        }

        if (firstException != null)
            throw firstException;
    }
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream resolved runtime targets inside endpoint code
//   New principle: resolve the command target in Application before binding projection and dispatch state
internal sealed class ScriptServiceRunCommandTargetResolver
    : ICommandTargetResolver<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunStartError>
{
    private readonly IScriptServiceAguiProjectionPort _projectionPort;

    public ScriptServiceRunCommandTargetResolver(IScriptServiceAguiProjectionPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public Task<CommandTargetResolution<ScriptServiceRunCommandTarget, ScriptServiceRunStartError>> ResolveAsync(
        ScriptServiceRunCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(command.RuntimeActorId))
        {
            return Task.FromResult(
                CommandTargetResolution<ScriptServiceRunCommandTarget, ScriptServiceRunStartError>.Failure(
                    ScriptServiceRunStartError.RuntimeActorUnavailable(
                        "Script runtime actor is not available. The service may not be activated.")));
        }

        if (string.IsNullOrWhiteSpace(command.RunId))
        {
            return Task.FromResult(
                CommandTargetResolution<ScriptServiceRunCommandTarget, ScriptServiceRunStartError>.Failure(
                    ScriptServiceRunStartError.InvalidArgument("runId", "Run id is required.")));
        }

        return Task.FromResult(
            CommandTargetResolution<ScriptServiceRunCommandTarget, ScriptServiceRunStartError>.Success(
                new ScriptServiceRunCommandTarget(command.RuntimeActorId, command.RunId, _projectionPort)));
    }
}

internal sealed class ScriptServiceRunCommandTargetBinder
    : ICommandObservationLifecycle<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>
{
    private readonly IScriptServiceAguiProjectionPort _projectionPort;

    public ScriptServiceRunCommandTargetBinder(IScriptServiceAguiProjectionPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    // Refactor (iter37/cluster-037-gagentservice-binders-attach-existing):
    //   Old pattern: GAgentService interaction binders synchronously prime projection sessions before dispatch(request-path projection activation in BindAsync).
    //   New principle: Attach-only to existing projection sessions/materialization leases via capability-specific attach-existing ports.
    //   Cold sessions return ProjectionUnavailable / pending before dispatch; no top-level live-observation exception.
    public async Task<CommandObservationBindingResult<ScriptServiceRunStartError>> BindAsync(
        ScriptServiceRunCommand command,
        CommandDispatchExecution<ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var context = execution.Context;

        var chatRequest = new ChatRequestEvent
        {
            Prompt = command.Prompt ?? string.Empty,
            SessionId = command.SessionId ?? string.Empty,
            ScopeId = command.ScopeId ?? string.Empty,
        };
        CopyHeaders(command.Headers, chatRequest.Metadata);
        var inputPayload = Any.Pack(chatRequest);
        var eventChannel = new EventChannel<AGUIEvent>();

        var attachment = await _projectionPort.AttachExistingRunProjectionAsync(
            target.ActorId,
            command.RunId,
            eventChannel,
            ct);
        if (attachment == null)
        {
            return CommandObservationBindingResult<ScriptServiceRunStartError>.Failure(
                ScriptServiceRunStartError.ProjectionUnavailable(
                    "Script execution projection pipeline is unavailable."));
        }

        target.BindRunInput(inputPayload, command, context);
        target.BindLiveObservation(attachment.ProjectionLease, attachment.LiveSinkLease, eventChannel);
        return CommandObservationBindingResult<ScriptServiceRunStartError>.Success();
    }

    private static void CopyHeaders(
        IReadOnlyDictionary<string, string>? source,
        IDictionary<string, string> destination)
    {
        if (source == null)
            return;

        foreach (var (key, value) in source)
        {
            if (!string.IsNullOrWhiteSpace(key) && value != null)
                destination[key] = value;
        }
    }
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream built runtime envelopes inline in the Host endpoint
//   New principle: keep envelope construction in the command interaction pipeline
internal sealed class ScriptServiceRunEnvelopeFactory : ICommandEnvelopeFactory<ScriptServiceRunCommand>
{
    public EventEnvelope CreateEnvelope(ScriptServiceRunCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            Payload = Any.Pack(new Empty()),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream inline orchestration in endpoints
//   New principle: use existing ICommandInteractionService skeleton with ScriptServiceRunCommand and Application-owned service-run registration decorator
internal sealed class ScriptServiceRunCommandDispatcher
    : ICommandTargetDispatcher<ScriptServiceRunCommandTarget>
{
    private readonly IScriptRuntimeCommandPort _runtimeCommandPort;

    public ScriptServiceRunCommandDispatcher(IScriptRuntimeCommandPort runtimeCommandPort)
    {
        _runtimeCommandPort = runtimeCommandPort ?? throw new ArgumentNullException(nameof(runtimeCommandPort));
    }

    public async Task<DispatchAdmission> DispatchAsync(
        ScriptServiceRunCommandTarget target,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(envelope);

        await _runtimeCommandPort.RunRuntimeAsync(
            target.ActorId,
            target.RunId,
            target.CommandId,
            target.CorrelationId,
            target.InputPayload?.Clone(),
            target.ScriptRevision,
            target.DefinitionActorId,
            target.InputPayload?.TypeUrl ?? string.Empty,
            target.ScopeId,
            ct);
        return DispatchAdmissionFactory.Create(target.TargetId, envelope);
    }
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream accepted state was implicit in endpoint-local variables
//   New principle: return an honest accepted receipt with separate run, command, and correlation identities
internal sealed class ScriptServiceRunAcceptedReceiptFactory
    : ICommandReceiptFactory<ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt>
{
    public ScriptServiceRunAcceptedReceipt Create(
        ScriptServiceRunCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new ScriptServiceRunAcceptedReceipt(
            ActorId: target.ActorId,
            RunId: target.RunId,
            CommandId: context.CommandId,
            CorrelationId: context.CorrelationId,
            AcceptedAt: DateTimeOffset.UtcNow);
    }
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream terminal-frame checks lived in endpoint helpers
//   New principle: completion is resolved by the shared interaction policy
internal sealed class ScriptServiceRunCompletionPolicy
    : ICommandCompletionPolicy<AGUIEvent, ScriptServiceRunCompletionStatus>
{
    public ScriptServiceRunCompletionStatus IncompleteCompletion => ScriptServiceRunCompletionStatus.Incomplete;

    public bool TryResolve(
        AGUIEvent evt,
        out ScriptServiceRunCompletionStatus completion)
    {
        ArgumentNullException.ThrowIfNull(evt);

        switch (evt.EventCase)
        {
            case AGUIEvent.EventOneofCase.RunFinished:
                completion = ScriptServiceRunCompletionStatus.RunFinished;
                return true;
            case AGUIEvent.EventOneofCase.RunError:
                completion = ScriptServiceRunCompletionStatus.RunError;
                return true;
            default:
                completion = ScriptServiceRunCompletionStatus.Incomplete;
                return false;
        }
    }
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream emitted synthetic failures from Host-local orchestration
//   New principle: finalization is an Application interaction concern after dispatch observation completes
internal sealed class ScriptServiceRunFinalizeEmitter
    : ICommandFinalizeEmitter<ScriptServiceRunAcceptedReceipt, ScriptServiceRunCompletionStatus, AGUIEvent>
{
    public Task EmitAsync(
        ScriptServiceRunAcceptedReceipt receipt,
        ScriptServiceRunCompletionStatus completion,
        bool completed,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(emitAsync);

        if (completed)
            return Task.CompletedTask;

        return emitAsync(
            new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    Message = "Script service chat stream ended before a terminal event was observed.",
                },
            },
            ct).AsTask();
    }
}

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream had no typed durable completion hook
//   New principle: keep the durable completion extension point in the shared interaction skeleton
internal sealed class ScriptServiceRunDurableCompletionResolver
    : ICommandDurableCompletionResolver<ScriptServiceRunAcceptedReceipt, ScriptServiceRunCompletionStatus>
{
    public Task<CommandDurableCompletionObservation<ScriptServiceRunCompletionStatus>> ResolveAsync(
        ScriptServiceRunAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        _ = receipt;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CommandDurableCompletionObservation<ScriptServiceRunCompletionStatus>.Incomplete);
    }
}
