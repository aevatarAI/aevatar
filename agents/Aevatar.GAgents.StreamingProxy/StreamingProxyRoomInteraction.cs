using System.Runtime.ExceptionServices;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter343/cluster-001-chat-session-command-identity):
//   Old pattern: Chat interaction commands reuse SessionId as CommandId and CorrelationId.
//   New principle: Generate or carry distinct command/correlation identifiers while keeping SessionId only as conversation/projection session identity.
public sealed record StreamingProxyRoomChatCommand(
    string RoomId,
    string ScopeId,
    string Prompt,
    string SessionId,
    string? AccessToken = null,
    string? PreferredRoute = null,
    string? DefaultModel = null,
    string? CommandId = null,
    string? CorrelationId = null)
    : ICommandContextSeed
{
    public IReadOnlyDictionary<string, string>? Headers => null;
}

// Refactor (iter21/cluster-002-request-path-projection-session-priming):
//   Old pattern: endpoint-local state mixed accepted dispatch with terminal observation.
//   New principle: receipt exposes accepted dispatch identity; projection/durable completion stays separate.
public sealed record StreamingProxyRoomChatAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    string SessionId);

public enum StreamingProxyRoomChatStartError
{
    None = 0,
    RoomNotFound = 1,
    ProjectionUnavailable = 2,
}

internal sealed class StreamingProxyRoomChatCommandTarget
    : IActorCommandDispatchTarget,
      ICommandEventTarget<StreamingProxyRoomSessionEnvelope>,
      ICommandInteractionCleanupTarget<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus>,
      ICommandDispatchCleanupAware
{
    private readonly IStreamingProxyRoomSessionProjectionPort _projectionPort;

    public StreamingProxyRoomChatCommandTarget(
        IActor actor,
        IStreamingProxyRoomSessionProjectionPort projectionPort)
    {
        // Refactor (iter21/cluster-002-request-path-projection-session-priming):
        //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
        //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public IActor Actor { get; }
    public string TargetId => Actor.Id;
    public string ActorId => Actor.Id;
    public string SessionId { get; private set; } = string.Empty;
    public IStreamingProxyRoomSessionProjectionLease? ProjectionLease { get; private set; }
    public IAsyncDisposable? LiveSinkLease { get; private set; }
    public IEventSink<StreamingProxyRoomSessionEnvelope>? LiveSink { get; private set; }

    public void BindLiveObservation(
        IStreamingProxyRoomSessionProjectionLease projectionLease,
        IAsyncDisposable? liveSinkLease,
        IEventSink<StreamingProxyRoomSessionEnvelope> sink,
        string sessionId)
    {
        // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
        //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
        //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
        ProjectionLease = projectionLease ?? throw new ArgumentNullException(nameof(projectionLease));
        LiveSinkLease = liveSinkLease;
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("Session id is required.", nameof(sessionId))
            : sessionId;
    }

    public IEventSink<StreamingProxyRoomSessionEnvelope> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("StreamingProxy room chat live sink is not bound.");

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(ct);

    public Task ReleaseAfterInteractionAsync(
        StreamingProxyRoomChatAcceptedReceipt receipt,
        CommandInteractionCleanupContext<StreamingProxyProjectionCompletionStatus> cleanup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(cleanup);
        return ReleaseAsync(ct);
    }

    private async Task ReleaseAsync(CancellationToken ct)
    {
        Exception? firstException = null;
        var projectionLease = ProjectionLease;
        var liveSinkLease = LiveSinkLease;
        var sink = LiveSink;

        if (projectionLease != null && sink != null)
        {
            try
            {
                await _projectionPort.DetachReleaseAndDisposeAsync(
                    projectionLease,
                    liveSinkLease,
                    sink,
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
        else if (sink != null)
        {
            try
            {
                sink.Complete();
                await sink.DisposeAsync();
                LiveSinkLease = null;
                LiveSink = null;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }
}

internal sealed class StreamingProxyRoomChatCommandTargetResolver
    : ICommandTargetResolver<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatStartError>
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IStreamingProxyRoomSessionProjectionPort _projectionPort;

    public StreamingProxyRoomChatCommandTargetResolver(
        IActorRuntime actorRuntime,
        IStreamingProxyRoomSessionProjectionPort projectionPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<CommandTargetResolution<StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatStartError>> ResolveAsync(
        StreamingProxyRoomChatCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var actor = await _actorRuntime.GetAsync(command.RoomId.Trim());
        if (actor == null)
        {
            return CommandTargetResolution<StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatStartError>.Failure(
                StreamingProxyRoomChatStartError.RoomNotFound);
        }

        return CommandTargetResolution<StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatStartError>.Success(
            new StreamingProxyRoomChatCommandTarget(actor, _projectionPort));
    }
}

internal sealed class StreamingProxyRoomObservationLifecycle
    : ICommandObservationLifecycle<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>
{
    // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
    //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
    //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
    private readonly IStreamingProxyRoomSessionProjectionPort _projectionPort;

    public StreamingProxyRoomObservationLifecycle(
        IStreamingProxyRoomSessionProjectionPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<CommandObservationBindingResult<StreamingProxyRoomChatStartError>> BindAsync(
        StreamingProxyRoomChatCommand command,
        CommandDispatchExecution<StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
        //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
        //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var sink = new EventChannel<StreamingProxyRoomSessionEnvelope>();
        try
        {
            var attachment = await _projectionPort.AttachExistingChatProjectionAsync(
                target.ActorId,
                command.SessionId,
                sink,
                ct);
            if (attachment == null)
            {
                sink.Complete();
                await sink.DisposeAsync();
                return CommandObservationBindingResult<StreamingProxyRoomChatStartError>.Failure(
                    StreamingProxyRoomChatStartError.ProjectionUnavailable);
            }

            target.BindLiveObservation(
                attachment.ProjectionLease,
                attachment.LiveSinkLease,
                sink,
                command.SessionId);
            return CommandObservationBindingResult<StreamingProxyRoomChatStartError>.Success();
        }
        catch
        {
            sink.Complete();
            await sink.DisposeAsync();
            throw;
        }
    }
}

internal sealed class StreamingProxyRoomChatCommandEnvelopeFactory
    : ICommandEnvelopeFactory<StreamingProxyRoomChatCommand>
{
    public EventEnvelope CreateEnvelope(StreamingProxyRoomChatCommand command, CommandContext context)
    {
        // Refactor (iter343/cluster-001-chat-session-command-identity):
        //   Old pattern: Chat interaction commands reuse SessionId as CommandId and CorrelationId.
        //   New principle: Generate or carry distinct command/correlation identifiers while keeping SessionId only as conversation/projection session identity.
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = command.SessionId,
            ScopeId = command.ScopeId,
        };
        chatRequest.LlmControl = new LLMControlContext(
            NyxIdAccessToken: Normalize(command.AccessToken),
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            ModelOverride: Normalize(command.DefaultModel),
            NyxIdRoutePreference: Normalize(command.PreferredRoute),
            MaxToolRoundsOverride: null,
            UserMemoryPrompt: null).ToPayload();

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(chatRequest),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = context.TargetId } },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class StreamingProxyRoomChatAcceptedReceiptFactory
    : ICommandReceiptFactory<StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt>
{
    public StreamingProxyRoomChatAcceptedReceipt Create(
        StreamingProxyRoomChatCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new StreamingProxyRoomChatAcceptedReceipt(
            target.ActorId,
            context.CommandId,
            context.CorrelationId,
            target.SessionId);
    }
}

internal sealed class StreamingProxyRoomChatCompletionPolicy
    : ICommandCompletionPolicy<StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>
{
    public StreamingProxyProjectionCompletionStatus IncompleteCompletion => StreamingProxyProjectionCompletionStatus.Unknown;

    public bool TryResolve(
        StreamingProxyRoomSessionEnvelope evt,
        out StreamingProxyProjectionCompletionStatus completion)
    {
        ArgumentNullException.ThrowIfNull(evt);

        completion = StreamingProxyProjectionCompletionStatus.Unknown;
        if (evt.Envelope == null ||
            !StreamingProxyRoomInteractionHelpers.TryGetTerminalEvent(evt.Envelope, out var terminalEvent))
        {
            return false;
        }

        completion = terminalEvent.Status == StreamingProxyChatSessionTerminalStatus.Failed
            ? StreamingProxyProjectionCompletionStatus.Failed
            : StreamingProxyProjectionCompletionStatus.Completed;
        return true;
    }
}

internal sealed class StreamingProxyRoomChatFinalizeEmitter
    : ICommandFinalizeEmitter<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus, StreamingProxyRoomSessionEnvelope>
{
    public Task EmitAsync(
        StreamingProxyRoomChatAcceptedReceipt receipt,
        StreamingProxyProjectionCompletionStatus completion,
        bool completed,
        Func<StreamingProxyRoomSessionEnvelope, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        // Refactor (iter21/cluster-002-request-path-projection-session-priming):
        //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
        //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(emitAsync);

        if (completed)
            return Task.CompletedTask;

        return emitAsync(
            new StreamingProxyRoomSessionEnvelope
            {
                Envelope = StreamingProxyRoomInteractionHelpers.CreateTerminalEnvelope(
                    receipt.ActorId,
                    receipt.SessionId,
                    completion == StreamingProxyProjectionCompletionStatus.Failed
                        ? StreamingProxyChatSessionTerminalStatus.Failed
                        : StreamingProxyChatSessionTerminalStatus.Failed,
                    "StreamingProxy completion timed out."),
            },
            ct).AsTask();
    }
}

internal sealed class StreamingProxyRoomChatDurableCompletionResolverAdapter
    : ICommandDurableCompletionResolver<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus>
{
    private readonly StreamingProxyChatDurableCompletionResolver _inner;

    public StreamingProxyRoomChatDurableCompletionResolverAdapter(
        StreamingProxyChatDurableCompletionResolver inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<CommandDurableCompletionObservation<StreamingProxyProjectionCompletionStatus>> ResolveAsync(
        StreamingProxyRoomChatAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var completion = await _inner.ResolveAsync(receipt.ActorId, receipt.SessionId, ct);
        return completion switch
        {
            StreamingProxyProjectionCompletionStatus.Completed => new(true, completion),
            StreamingProxyProjectionCompletionStatus.Failed => new(true, completion),
            _ => CommandDurableCompletionObservation<StreamingProxyProjectionCompletionStatus>.Incomplete,
        };
    }
}

internal sealed class StreamingProxyRoomChatOutputStream
    : IEventOutputStream<StreamingProxyRoomSessionEnvelope, StreamingProxyRoomSessionEnvelope>
{
    public async Task PumpAsync(
        IAsyncEnumerable<StreamingProxyRoomSessionEnvelope> events,
        Func<StreamingProxyRoomSessionEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<StreamingProxyRoomSessionEnvelope, bool>? shouldStop = null,
        CancellationToken ct = default)
    {
        // Refactor (iter21/cluster-002-request-path-projection-session-priming):
        //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
        //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var sawActivity = false;
        var sawAgentMessage = false;
        var enumerator = events.GetAsyncEnumerator(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(sawAgentMessage
                    ? StreamingProxyDefaults.IdleCompletionTimeoutMs
                    : sawActivity
                        ? StreamingProxyDefaults.PostTopicTimeoutMs
                        : StreamingProxyDefaults.InitialResponseTimeoutMs);

                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().AsTask().WaitAsync(idleCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return;
                }

                if (!hasNext)
                    return;

                var evt = enumerator.Current;
                var signal = StreamingProxyRoomInteractionHelpers.ResolveSignal(evt);
                if (signal.HasValue)
                {
                    sawActivity = true;
                    if (signal == StreamingProxyEndpoints.StreamingProxyStreamSignal.AgentMessage)
                        sawAgentMessage = true;
                }

                await emitAsync(evt, ct);

                if (shouldStop?.Invoke(evt) == true)
                    return;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync();
            }
            catch (NotSupportedException)
            {
                // ChannelReader.ReadAllAsync may not support async enumerator disposal after an idle timeout.
            }
        }
    }
}

internal static class StreamingProxyRoomInteractionHelpers
{
    public static StreamingProxyEndpoints.StreamingProxyStreamSignal? ResolveSignal(StreamingProxyRoomSessionEnvelope sessionEnvelope)
    {
        var envelope = sessionEnvelope.Envelope;
        if (envelope == null)
            return null;

        if (TryGetTerminalEvent(envelope, out var terminalEvent))
        {
            return terminalEvent.Status == StreamingProxyChatSessionTerminalStatus.Failed
                ? StreamingProxyEndpoints.StreamingProxyStreamSignal.RunFailed
                : StreamingProxyEndpoints.StreamingProxyStreamSignal.RunFinished;
        }

        var payload = envelope.Payload;
        if (CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var observedPayload, out _, out _) &&
            observedPayload != null)
        {
            payload = observedPayload;
        }

        if (payload == null)
            return null;

        if (payload.Is(GroupChatTopicEvent.Descriptor))
            return StreamingProxyEndpoints.StreamingProxyStreamSignal.TopicStarted;
        if (payload.Is(GroupChatMessageEvent.Descriptor))
            return StreamingProxyEndpoints.StreamingProxyStreamSignal.AgentMessage;

        return null;
    }

    public static bool TryGetTerminalEvent(
        EventEnvelope envelope,
        out StreamingProxyChatSessionTerminalStateChanged terminalEvent)
    {
        terminalEvent = new StreamingProxyChatSessionTerminalStateChanged();
        if (CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out _, out _) &&
            payload?.Is(StreamingProxyChatSessionTerminalStateChanged.Descriptor) == true)
        {
            terminalEvent = payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>();
            return !string.IsNullOrWhiteSpace(terminalEvent.SessionId);
        }

        if (envelope.Payload?.Is(StreamingProxyChatSessionTerminalStateChanged.Descriptor) == true)
        {
            terminalEvent = envelope.Payload.Unpack<StreamingProxyChatSessionTerminalStateChanged>();
            return !string.IsNullOrWhiteSpace(terminalEvent.SessionId);
        }

        return false;
    }

    public static EventEnvelope CreateTerminalEnvelope(
        string actorId,
        string sessionId,
        StreamingProxyChatSessionTerminalStatus status,
        string? errorMessage)
    {
        var terminalEvent = new StreamingProxyChatSessionTerminalStateChanged
        {
            SessionId = sessionId,
            Status = status,
            TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ErrorMessage = errorMessage ?? string.Empty,
        };
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(terminalEvent),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute
                {
                    TargetActorId = actorId,
                },
            },
        };
    }
}
