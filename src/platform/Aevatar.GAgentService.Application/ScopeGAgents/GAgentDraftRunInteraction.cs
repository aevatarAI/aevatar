using System.Runtime.ExceptionServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.AGUI.Contracts;
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
        string diagnosticClrTypeName,
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        DiagnosticClrTypeName = string.IsNullOrWhiteSpace(diagnosticClrTypeName)
            ? throw new ArgumentException("Diagnostic CLR type name is required.", nameof(diagnosticClrTypeName))
            : diagnosticClrTypeName.Trim();
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _terminalProjectionPort = terminalProjectionPort ?? throw new ArgumentNullException(nameof(terminalProjectionPort));
    }

    public IActor Actor { get; }
    public string DiagnosticClrTypeName { get; }
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
    private readonly IAgentKindVerifier? _agentKindVerifier;
    private readonly IAgentKindRegistry? _agentKindRegistry;

    public GAgentDraftRunCommandTargetResolver(
        IActorRuntime actorRuntime,
        IGAgentDraftRunProjectionPort projectionPort,
        IGAgentRunTerminalProjectionPort terminalProjectionPort,
        IAgentKindVerifier? agentKindVerifier = null,
        IAgentKindRegistry? agentKindRegistry = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _terminalProjectionPort = terminalProjectionPort ?? throw new ArgumentNullException(nameof(terminalProjectionPort));
        _agentKindVerifier = agentKindVerifier;
        _agentKindRegistry = agentKindRegistry;
    }

    public async Task<CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>> ResolveAsync(
        GAgentDraftRunCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var agentKind = NormalizeOptional(command.AgentKind);
        var implementation = ResolveExpectedImplementation(agentKind);
        if (implementation is null)
        {
            return CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>.Failure(
                GAgentDraftRunStartError.UnknownAgentKind);
        }

        agentKind = implementation.Metadata.Kind;
        var diagnosticActorTypeName = implementation.Metadata.ImplementationClrTypeName;
        var preferredActorId = string.IsNullOrWhiteSpace(command.PreferredActorId)
            ? null
            : command.PreferredActorId.Trim();

        IActor actor;
        if (preferredActorId is not null)
        {
            var existingActor = await _actorRuntime.GetAsync(preferredActorId);
            if (existingActor != null)
            {
                if (!await MatchesExpectedKindAsync(existingActor, agentKind, ct))
                {
                    return CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>.Failure(
                        GAgentDraftRunStartError.ActorKindMismatch);
                }

                actor = existingActor;
            }
            else
            {
                actor = await _actorRuntime.CreateByKindAsync(agentKind, preferredActorId, ct);
            }
        }
        else
        {
            actor = await _actorRuntime.CreateByKindAsync(agentKind, null, ct);
        }

        return CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>.Success(
            new GAgentDraftRunCommandTarget(
                actor,
                diagnosticActorTypeName,
                _projectionPort,
                _terminalProjectionPort));
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private AgentImplementation? ResolveExpectedImplementation(string? agentKind)
    {
        if (string.IsNullOrWhiteSpace(agentKind) || _agentKindRegistry == null)
            return null;

        try
        {
            return _agentKindRegistry.Resolve(agentKind);
        }
        catch (UnknownAgentKindException)
        {
            return null;
        }
    }

    private async Task<bool> MatchesExpectedKindAsync(
        IActor actor,
        string expectedKind,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKind);

        if (_agentKindVerifier == null)
            return false;

        return await _agentKindVerifier.IsExpectedKindAsync(actor.Id, expectedKind, ct);
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
        // Refactor (iter37/cluster-037-gagentservice-binders-attach-existing):
        //   Old pattern: GAgentService interaction binders synchronously prime projection sessions before dispatch(request-path projection activation in BindAsync).
        //   New principle: Attach-only to existing projection sessions/materialization leases via capability-specific attach-existing ports.
        //   Cold sessions return ProjectionUnavailable / pending before dispatch; no top-level live-observation exception.
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var context = execution.Context;
        var sink = new EventChannel<AGUIEvent>();
        IGAgentRunTerminalProjectionLease? terminalProjectionLease = null;

        try
        {
            terminalProjectionLease = await _terminalProjectionPort.AttachExistingProjectionAsync(
                target.ActorId,
                context.CorrelationId,
                GAgentRunTerminalInteractionKind.DraftRun,
                ct);
            if (terminalProjectionLease == null)
                return await FailProjectionUnavailableAsync(sink);

            target.BindTerminalProjection(terminalProjectionLease);

            var attachment = await _projectionPort.AttachExistingActorProjectionAsync(
                target.ActorId,
                context.CommandId,
                sink,
                ct);

            if (attachment == null)
            {
                await _terminalProjectionPort.ReleaseProjectionAsync(terminalProjectionLease, ct);
                target.BindTerminalProjection(null);
                return await FailProjectionUnavailableAsync(sink);
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

    private static async Task<CommandObservationBindingResult<GAgentDraftRunStartError>> FailProjectionUnavailableAsync(
        IEventSink<AGUIEvent> sink)
    {
        sink.Complete();
        await sink.DisposeAsync();
        return CommandObservationBindingResult<GAgentDraftRunStartError>.Failure(
            GAgentDraftRunStartError.ProjectionUnavailable);
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
            CommandAttemptId = context.CommandId,
        };

        CopyHeaders(chatRequest.Headers, context.Headers);
        // Refactor (iter1353/cluster-001): Old pattern: ChatRequestEvent control was rebuilt from Metadata or legacy command scalars.
        // New principle: command-level ToolContext and LlmControl are serialized directly into the event payload.
        chatRequest.ToolContext = (command.ToolContext ?? AgentToolExecutionContext.Empty).ToPayload();
        chatRequest.LlmControl = (command.LlmControl ?? new LLMControlContext(
            NyxIdAccessToken: Normalize(command.NyxIdAccessToken),
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            ModelOverride: Normalize(command.ModelOverride),
            NyxIdRoutePreference: Normalize(command.PreferredLlmRoute),
            MaxToolRoundsOverride: null,
            UserMemoryPrompt: null)).ToPayload();
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

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
            FileRef = source.FileRef?.Clone(),
        };
    }

    private static void CopyHeaders(
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
            target.DiagnosticClrTypeName,
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
                completion = string.Equals(
                    evt.RunError.Code,
                    GAgentRunFailureCodes.OutcomeUncertain,
                    StringComparison.Ordinal)
                    ? GAgentDraftRunCompletionStatus.OutcomeUncertain
                    : GAgentDraftRunCompletionStatus.Failed;
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
                    // Refactor (iter98/cluster-790): Old: terminal RunFinished carried no typed result, so consumers needed backend text-frame synthesis for missed live deltas. New: result packs GAgentDraftRunResultPayload and consumers use result.output fallback.
                    Result = Any.Pack(new GAgentDraftRunResultPayload()),
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
            GAgentRunTerminalStatus.OutcomeUncertain => new(true, GAgentDraftRunCompletionStatus.OutcomeUncertain),
            _ => CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete,
        };
}
