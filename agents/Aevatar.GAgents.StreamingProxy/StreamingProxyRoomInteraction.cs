using System.Runtime.ExceptionServices;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.StreamingProxy;

// Refactor (iter20/cluster-002):
//   Old pattern: 请求路径临时启动 projection session 等待完成
//   New principle: reuse CQRS interaction binders + attach-only projection lifecycle
public sealed record StreamingProxyRoomChatCommand(
    string RoomId,
    string ScopeId,
    string Prompt,
    string SessionId);

// Refactor (iter20/cluster-002):
//   Old pattern: 请求路径临时启动 projection session 等待完成
//   New principle: reuse CQRS interaction binders + attach-only projection lifecycle
public sealed record StreamingProxyRoomChatAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    string SessionId);

// Refactor (iter20/cluster-002):
//   Old pattern: 请求路径临时启动 projection session 等待完成
//   New principle: reuse CQRS interaction binders + attach-only projection lifecycle
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
        // Refactor (iter20/cluster-002):
        //   Old pattern: 请求路径临时启动 projection session 等待完成
        //   New principle: reuse CQRS interaction binders + attach-only projection lifecycle
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

internal sealed class StreamingProxyRoomChatCommandTargetBinder
    : ICommandTargetBinder<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatStartError>
{
    private readonly IStreamingProxyRoomSessionProjectionPort _projectionPort;

    public StreamingProxyRoomChatCommandTargetBinder(
        IStreamingProxyRoomSessionProjectionPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<CommandTargetBindingResult<StreamingProxyRoomChatStartError>> BindAsync(
        StreamingProxyRoomChatCommand command,
        StreamingProxyRoomChatCommandTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        // Refactor (iter20/cluster-002):
        //   Old pattern: 请求路径临时启动 projection session 等待完成
        //   New principle: reuse CQRS interaction binders + attach-only projection lifecycle
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var sink = new EventChannel<StreamingProxyRoomSessionEnvelope>();
        try
        {
            var attachment = await _projectionPort.EnsureAndAttachLeaseAsync(
                token => _projectionPort.EnsureChatProjectionAsync(
                    target.ActorId,
                    command.SessionId,
                    token),
                sink,
                ct);
            if (attachment == null)
            {
                sink.Complete();
                await sink.DisposeAsync();
                return CommandTargetBindingResult<StreamingProxyRoomChatStartError>.Failure(
                    StreamingProxyRoomChatStartError.ProjectionUnavailable);
            }

            target.BindLiveObservation(
                attachment.ProjectionLease,
                attachment.LiveSinkLease,
                sink,
                command.SessionId);
            return CommandTargetBindingResult<StreamingProxyRoomChatStartError>.Success();
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
        // Refactor (iter20/cluster-002):
        //   Old pattern: 请求路径临时启动 projection session 等待完成
        //   New principle: reuse CQRS interaction binders + attach-only projection lifecycle
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = command.SessionId,
            ScopeId = command.ScopeId,
        };

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
        // Refactor (iter20/cluster-002):
        //   Old pattern: 请求路径临时启动 projection session 等待完成
        //   New principle: reuse CQRS interaction binders + attach-only projection lifecycle
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
        // Refactor (iter20/cluster-002):
        //   Old pattern: 请求路径临时启动 projection session 等待完成
        //   New principle: reuse CQRS interaction binders + attach-only projection lifecycle
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var sawActivity = false;
        var sawAgentMessage = false;
        await using var enumerator = events.GetAsyncEnumerator(ct);
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
