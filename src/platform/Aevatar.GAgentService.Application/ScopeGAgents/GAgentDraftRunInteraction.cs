using System.Runtime.ExceptionServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Presentation.AGUI;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.ScopeGAgents;

internal sealed class GAgentDraftRunCommandTarget
    : IActorCommandDispatchTarget,
      ICommandEventTarget<AGUIEvent>,
      ICommandInteractionCleanupTarget<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus>,
      ICommandDispatchCleanupAware
{
    private readonly IGAgentDraftRunProjectionPort _projectionPort;
    private readonly IGAgentRunTerminalProjectionPort _terminalProjectionPort;

    public GAgentDraftRunCommandTarget(
        IActor actor,
        string actorTypeName,
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        ActorTypeName = string.IsNullOrWhiteSpace(actorTypeName)
            ? throw new ArgumentException("Actor type name is required.", nameof(actorTypeName))
            : actorTypeName.Trim();
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _terminalProjectionPort = terminalProjectionPort ?? throw new ArgumentNullException(nameof(terminalProjectionPort));
    }

    public IActor Actor { get; }
    public string ActorTypeName { get; }
    public string TargetId => Actor.Id;
    public string ActorId => Actor.Id;
    public string SessionId { get; private set; } = string.Empty;
    public IGAgentDraftRunProjectionLease? ProjectionLease { get; private set; }
    public IGAgentRunTerminalProjectionLease? TerminalProjectionLease { get; private set; }
    public IAsyncDisposable? LiveSinkLease { get; private set; }
    public IEventSink<AGUIEvent>? LiveSink { get; private set; }
    private IEventSink<AGUIEvent>? InteractionLiveSink { get; set; }
    public bool AwaitingApprovalTerminalFact { get; private set; }

    public void BindTerminalProjection(IGAgentRunTerminalProjectionLease? lease)
    {
        TerminalProjectionLease = lease;
    }

    public void BindLiveObservation(
        IGAgentDraftRunProjectionLease lease,
        IAsyncDisposable? liveSinkLease,
        IEventSink<AGUIEvent> sink,
        string sessionId)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: command preparation could attach projection/session leases and mix read-side observation into dispatch admission.
        //   New principle: live observation is an explicit interaction phase that starts before dispatch; PrepareAsync and dispatch-only callers stay free of read-side lifecycle work
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
        LiveSinkLease = liveSinkLease;
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
        InteractionLiveSink = new ApprovalObservingEventSink(sink, MarkAwaitingApprovalTerminalFact);
        SessionId = sessionId;
    }

    public IEventSink<AGUIEvent> RequireLiveSink() =>
        InteractionLiveSink ?? throw new InvalidOperationException("GAgent draft-run live sink is not bound.");

    public void MarkAwaitingApprovalTerminalFact()
    {
        AwaitingApprovalTerminalFact = true;
    }

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(ct);

    public Task ReleaseAfterInteractionAsync(
        GAgentDraftRunAcceptedReceipt receipt,
        CommandInteractionCleanupContext<GAgentDraftRunCompletionStatus> cleanup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(cleanup);
        return ReleaseAsync(ShouldReleaseTerminalProjection(cleanup), ct);
    }

    private bool ShouldReleaseTerminalProjection(
        CommandInteractionCleanupContext<GAgentDraftRunCompletionStatus> cleanup) =>
        !cleanup.ObservedCompleted ||
        cleanup.ObservedCompletion != GAgentDraftRunCompletionStatus.TextMessageCompleted ||
        !AwaitingApprovalTerminalFact;

    private async Task ReleaseAsync(CancellationToken ct) =>
        await ReleaseAsync(releaseTerminalProjection: true, ct);

    private async Task ReleaseAsync(bool releaseTerminalProjection, CancellationToken ct)
    {
        Exception? firstException = null;
        var projectionLease = ProjectionLease;
        var sink = LiveSink;

        if (projectionLease != null && sink != null)
        {
            try
            {
                await _projectionPort.DetachReleaseAndDisposeAsync(
                    projectionLease,
                    LiveSinkLease,
                    sink,
                    null,
                    ct);
                ProjectionLease = null;
                LiveSinkLease = null;
                LiveSink = null;
                InteractionLiveSink = null;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }
        else
        {
            if (sink != null)
            {
                try
                {
                    sink.Complete();
                    await sink.DisposeAsync();
                    LiveSinkLease = null;
                    LiveSink = null;
                    InteractionLiveSink = null;
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
                    InteractionLiveSink = null;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
            }
        }

        var terminalProjectionLease = TerminalProjectionLease;
        if (releaseTerminalProjection && terminalProjectionLease != null)
        {
            try
            {
                await _terminalProjectionPort.ReleaseProjectionAsync(terminalProjectionLease, ct);
                TerminalProjectionLease = null;
                AwaitingApprovalTerminalFact = false;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }

    private sealed class ApprovalObservingEventSink(
        IEventSink<AGUIEvent> inner,
        Action markAwaitingApproval) : IEventSink<AGUIEvent>
    {
        public void Push(AGUIEvent evt) => inner.Push(evt);

        public ValueTask PushAsync(AGUIEvent evt, CancellationToken ct = default) =>
            inner.PushAsync(evt, ct);

        public void Complete() => inner.Complete();

        public async IAsyncEnumerable<AGUIEvent> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in inner.ReadAllAsync(ct))
            {
                if (evt.Custom?.Name == "TOOL_APPROVAL_REQUEST")
                    markAwaitingApproval();

                yield return evt;
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

internal sealed class GAgentDraftRunCommandTargetResolver
    : ICommandTargetResolver<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunStartError>
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IGAgentDraftRunProjectionPort _projectionPort;
    private readonly IGAgentRunTerminalProjectionPort _terminalProjectionPort;
    private readonly IAgentTypeVerifier? _agentTypeVerifier;

    public GAgentDraftRunCommandTargetResolver(
        IActorRuntime actorRuntime,
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort,
        IAgentTypeVerifier? agentTypeVerifier = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _terminalProjectionPort = terminalProjectionPort ?? throw new ArgumentNullException(nameof(terminalProjectionPort));
        _agentTypeVerifier = agentTypeVerifier;
    }

    public async Task<CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>> ResolveAsync(
        GAgentDraftRunCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var agentType = ScopeGAgentActorTypeResolver.Resolve(command.ActorTypeName);
        if (agentType is null)
        {
            return CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>.Failure(
                GAgentDraftRunStartError.UnknownActorType);
        }

        var preferredActorId = string.IsNullOrWhiteSpace(command.PreferredActorId)
            ? null
            : command.PreferredActorId.Trim();

        IActor actor;
        if (preferredActorId is not null)
        {
            var existingActor = await _actorRuntime.GetAsync(preferredActorId);
            if (existingActor != null)
            {
                if (!await MatchesExpectedTypeAsync(existingActor, agentType, ct))
                {
                    return CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>.Failure(
                        GAgentDraftRunStartError.ActorTypeMismatch);
                }

                actor = existingActor;
            }
            else
            {
                actor = await _actorRuntime.CreateAsync(agentType, preferredActorId, ct);
            }
        }
        else
        {
            actor = await _actorRuntime.CreateAsync(agentType, null, ct);
        }

        return CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>.Success(
            new GAgentDraftRunCommandTarget(actor, command.ActorTypeName, _projectionPort, _terminalProjectionPort));
    }

    private async Task<bool> MatchesExpectedTypeAsync(
        IActor actor,
        System.Type expectedType,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(expectedType);

        if (expectedType.IsAssignableFrom(actor.Agent.GetType()))
            return true;

        if (_agentTypeVerifier == null)
            return false;

        return await _agentTypeVerifier.IsExpectedAsync(actor.Id, expectedType, ct);
    }
}

internal sealed class GAgentDraftRunObservationLifecycle
    : ICommandObservationLifecycle<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError>
{
    private readonly IGAgentDraftRunProjectionPort _projectionPort;
    private readonly IGAgentRunTerminalProjectionPort _terminalProjectionPort;

    public GAgentDraftRunObservationLifecycle(
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _terminalProjectionPort = terminalProjectionPort ?? throw new ArgumentNullException(nameof(terminalProjectionPort));
    }

    public async Task<CommandObservationBindingResult<GAgentDraftRunStartError>> BindAsync(
        GAgentDraftRunCommand command,
        CommandDispatchExecution<GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: draft-run binder attached terminal/live projections during command preparation.
        //   New principle: interaction observation lifecycle starts read-side observation before dispatch without affecting dispatch-only command admission.
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var context = execution.Context;
        var sink = new EventChannel<AGUIEvent>();
        IGAgentRunTerminalProjectionLease? terminalProjectionLease = null;

        try
        {
            terminalProjectionLease = await _terminalProjectionPort.EnsureProjectionAsync(
                target.ActorId,
                context.CorrelationId,
                GAgentRunTerminalInteractionKind.DraftRun,
                ct);
            target.BindTerminalProjection(terminalProjectionLease);

            var attachment = await _projectionPort.EnsureAndAttachLeaseAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    target.ActorId,
                    context.CommandId,
                    token),
                sink,
                ct);

            if (attachment == null)
            {
                sink.Complete();
                await sink.DisposeAsync();
                throw new InvalidOperationException("GAgent draft-run projection pipeline is unavailable.");
            }

            target.BindLiveObservation(
                attachment.ProjectionLease,
                attachment.LiveSinkLease,
                sink,
                ResolveSessionId(command, context));
            return CommandObservationBindingResult<GAgentDraftRunStartError>.Success();
        }
        catch
        {
            if (terminalProjectionLease != null)
            {
                await _terminalProjectionPort.ReleaseProjectionAsync(terminalProjectionLease, ct);
                target.BindTerminalProjection(null);
            }

            sink.Complete();
            await sink.DisposeAsync();
            throw;
        }
    }

    private static string ResolveSessionId(
        GAgentDraftRunCommand command,
        CommandContext context) =>
        string.IsNullOrWhiteSpace(command.SessionId)
            ? (command.UseCorrelationIdAsFallbackSessionId ? context.CorrelationId : string.Empty)
            : command.SessionId.Trim();
}

internal sealed class GAgentDraftRunCommandEnvelopeFactory
    : ICommandEnvelopeFactory<GAgentDraftRunCommand>
{
    public EventEnvelope CreateEnvelope(GAgentDraftRunCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var sessionId = string.IsNullOrWhiteSpace(command.SessionId)
            ? (command.UseCorrelationIdAsFallbackSessionId ? context.CorrelationId : string.Empty)
            : command.SessionId.Trim();

        var chatRequest = new ChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = sessionId,
            ScopeId = command.ScopeId,
        };

        AppendMetadata(chatRequest.Metadata, context.Headers);
        if (!string.IsNullOrWhiteSpace(command.NyxIdAccessToken))
            chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = command.NyxIdAccessToken.Trim();
        if (!string.IsNullOrWhiteSpace(command.ModelOverride))
            chatRequest.Metadata[LLMRequestMetadataKeys.ModelOverride] = command.ModelOverride.Trim();
        if (!string.IsNullOrWhiteSpace(command.PreferredLlmRoute))
            chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = command.PreferredLlmRoute.Trim();
        if (command.InputParts is { Count: > 0 })
            chatRequest.InputParts.Add(command.InputParts.Select(ToProto));

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatRequest),
            Route = EnvelopeRouteSemantics.CreateDirect("api", context.TargetId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }

    private static ChatContentPart ToProto(GAgentDraftRunInputPart source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ChatContentPart
        {
            Kind = source.Kind switch
            {
                GAgentDraftRunInputPartKind.Text => ChatContentPartKind.Text,
                GAgentDraftRunInputPartKind.Image => ChatContentPartKind.Image,
                GAgentDraftRunInputPartKind.Audio => ChatContentPartKind.Audio,
                GAgentDraftRunInputPartKind.Video => ChatContentPartKind.Video,
                _ => ChatContentPartKind.Unspecified,
            },
            Text = source.Text ?? string.Empty,
            DataBase64 = source.DataBase64 ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            Uri = source.Uri ?? string.Empty,
            Name = source.Name ?? string.Empty,
        };
    }

    private static void AppendMetadata(
        Google.Protobuf.Collections.MapField<string, string> destination,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source == null || source.Count == 0)
            return;

        foreach (var (key, value) in source)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;

            destination[normalizedKey] = normalizedValue;
        }
    }
}

internal sealed class GAgentDraftRunAcceptedReceiptFactory
    : ICommandReceiptFactory<GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt>
{
    public GAgentDraftRunAcceptedReceipt Create(
        GAgentDraftRunCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new GAgentDraftRunAcceptedReceipt(
            target.ActorId,
            target.ActorTypeName,
            context.CommandId,
            context.CorrelationId,
            target.SessionId);
    }
}

internal sealed class GAgentDraftRunCompletionPolicy
    : ICommandCompletionPolicy<AGUIEvent, GAgentDraftRunCompletionStatus>
{
    public GAgentDraftRunCompletionStatus IncompleteCompletion => GAgentDraftRunCompletionStatus.Unknown;

    public bool TryResolve(
        AGUIEvent evt,
        out GAgentDraftRunCompletionStatus completion)
    {
        ArgumentNullException.ThrowIfNull(evt);

        completion = GAgentDraftRunCompletionStatus.Unknown;
        switch (evt.EventCase)
        {
            case AGUIEvent.EventOneofCase.TextMessageEnd:
                completion = GAgentDraftRunCompletionStatus.TextMessageCompleted;
                return true;
            case AGUIEvent.EventOneofCase.RunFinished:
                completion = GAgentDraftRunCompletionStatus.RunFinished;
                return true;
            case AGUIEvent.EventOneofCase.RunError:
                completion = GAgentDraftRunCompletionStatus.Failed;
                return true;
            default:
                return false;
        }
    }
}

internal sealed class GAgentDraftRunFinalizeEmitter
    : ICommandFinalizeEmitter<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus, AGUIEvent>
{
    public Task EmitAsync(
        GAgentDraftRunAcceptedReceipt receipt,
        GAgentDraftRunCompletionStatus completion,
        bool completed,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(emitAsync);

        if (!completed || completion != GAgentDraftRunCompletionStatus.TextMessageCompleted)
            return Task.CompletedTask;

        return emitAsync(
            new AGUIEvent
            {
                RunFinished = new RunFinishedEvent
                {
                    ThreadId = receipt.ActorId,
                    RunId = receipt.CommandId,
                },
            },
            ct).AsTask();
    }
}

internal sealed class GAgentDraftRunDurableCompletionResolver
    : ICommandDurableCompletionResolver<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus>
{
    private readonly IGAgentRunTerminalQueryPort _queryPort;

    public GAgentDraftRunDurableCompletionResolver(
        IGAgentRunTerminalQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public async Task<CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>> ResolveAsync(
        GAgentDraftRunAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        try
        {
            var snapshot = await _queryPort.GetByCorrelationIdAsync(receipt.ActorId, receipt.CorrelationId, ct);
            if (!MatchesReceipt(snapshot, receipt))
                snapshot = null;
            if (snapshot == null && !string.IsNullOrWhiteSpace(receipt.SessionId))
            {
                var sessionSnapshot = await _queryPort.GetBySessionIdAsync(receipt.ActorId, receipt.SessionId, ct);
                if (MatchesReceipt(sessionSnapshot, receipt))
                    snapshot = sessionSnapshot;
            }
            return Map(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete;
        }
    }

    private static bool MatchesReceipt(
        GAgentRunTerminalSnapshot? snapshot,
        GAgentDraftRunAcceptedReceipt receipt) =>
        snapshot != null &&
        string.Equals(snapshot.ActorId, receipt.ActorId, StringComparison.Ordinal) &&
        string.Equals(snapshot.CorrelationId, receipt.CorrelationId, StringComparison.Ordinal) &&
        snapshot.InteractionKind == GAgentRunTerminalInteractionKind.DraftRun;

    private static CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus> Map(
        GAgentRunTerminalSnapshot? snapshot) =>
        snapshot?.Status switch
        {
            GAgentRunTerminalStatus.TextMessageCompleted => new(true, GAgentDraftRunCompletionStatus.TextMessageCompleted),
            GAgentRunTerminalStatus.RunFinished => new(true, GAgentDraftRunCompletionStatus.RunFinished),
            GAgentRunTerminalStatus.Failed => new(true, GAgentDraftRunCompletionStatus.Failed),
            _ => CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete,
        };
}
