using System.Runtime.ExceptionServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatCommand(
    string ActorId,
    string ScopeId,
    string Prompt,
    string SessionId,
    string AccessToken,
    IReadOnlyList<NyxIdChatEndpoints.ContentPartDto>? InputParts,
    IReadOnlyDictionary<string, string>? Metadata)
    : ICommandContextSeed
{
    public string? CommandId => SessionId;
    public string? CorrelationId => SessionId;
    public IReadOnlyDictionary<string, string>? Headers => null;
}

public sealed record NyxIdApprovalCommand(
    string ActorId,
    string RequestId,
    bool Approved,
    string Reason,
    string SessionId)
    : ICommandContextSeed
{
    public string? CommandId => SessionId;
    public string? CorrelationId => SessionId;
    public IReadOnlyDictionary<string, string>? Headers => null;
}

public sealed record NyxIdChatAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    string SessionId);

public enum NyxIdChatStartError
{
    None = 0,
    ActorNotFound = 1,
    ProjectionUnavailable = 2,
}

public enum NyxIdChatCompletionStatus
{
    Unknown = 0,
    Completed = 1,
    Failed = 2,
}

internal sealed class NyxIdChatCommandTarget
    : IActorCommandDispatchTarget,
      ICommandEventTarget<AGUIEvent>,
      ICommandInteractionCleanupTarget<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus>,
      ICommandDispatchCleanupAware
{
    private readonly INyxIdChatSessionProjectionPort _projectionPort;

    public NyxIdChatCommandTarget(
        IActor actor,
        INyxIdChatSessionProjectionPort projectionPort)
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
    public INyxIdChatSessionProjectionLease? ProjectionLease { get; private set; }
    public IAsyncDisposable? LiveSinkLease { get; private set; }
    public IEventSink<AGUIEvent>? LiveSink { get; private set; }

    public void BindLiveObservation(
        INyxIdChatSessionProjectionLease projectionLease,
        IAsyncDisposable? liveSinkLease,
        IEventSink<AGUIEvent> sink,
        string sessionId)
    {
        ProjectionLease = projectionLease ?? throw new ArgumentNullException(nameof(projectionLease));
        LiveSinkLease = liveSinkLease;
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("Session id is required.", nameof(sessionId))
            : sessionId;
    }

    public IEventSink<AGUIEvent> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("NyxID chat live sink is not bound.");

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(ct);

    public Task ReleaseAfterInteractionAsync(
        NyxIdChatAcceptedReceipt receipt,
        CommandInteractionCleanupContext<NyxIdChatCompletionStatus> cleanup,
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

internal sealed class NyxIdChatCommandTargetResolver<TCommand>
    : ICommandTargetResolver<TCommand, NyxIdChatCommandTarget, NyxIdChatStartError>
{
    private readonly IActorRuntime _actorRuntime;
    private readonly INyxIdChatSessionProjectionPort _projectionPort;
    private readonly Func<TCommand, string> _actorIdResolver;

    public NyxIdChatCommandTargetResolver(
        IActorRuntime actorRuntime,
        INyxIdChatSessionProjectionPort projectionPort,
        Func<TCommand, string> actorIdResolver)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _actorIdResolver = actorIdResolver ?? throw new ArgumentNullException(nameof(actorIdResolver));
    }

    public async Task<CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>> ResolveAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var actor = await _actorRuntime.GetAsync(_actorIdResolver(command));
        if (actor == null)
        {
            return CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>.Failure(
                NyxIdChatStartError.ActorNotFound);
        }

        return CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>.Success(
            new NyxIdChatCommandTarget(actor, _projectionPort));
    }
}

internal sealed class NyxIdChatCommandTargetBinder<TCommand>
    : ICommandTargetBinder<TCommand, NyxIdChatCommandTarget, NyxIdChatStartError>
{
    private readonly INyxIdChatSessionProjectionPort _projectionPort;
    private readonly Func<TCommand, string> _sessionIdResolver;

    public NyxIdChatCommandTargetBinder(
        INyxIdChatSessionProjectionPort projectionPort,
        Func<TCommand, string> sessionIdResolver)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _sessionIdResolver = sessionIdResolver ?? throw new ArgumentNullException(nameof(sessionIdResolver));
    }

    public async Task<CommandTargetBindingResult<NyxIdChatStartError>> BindAsync(
        TCommand command,
        NyxIdChatCommandTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        // Refactor (iter20/cluster-002):
        //   Old pattern: 请求路径临时启动 projection session 等待完成
        //   New principle: reuse CQRS interaction binders + attach-only projection lifecycle
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var sessionId = _sessionIdResolver(command);
        var sink = new EventChannel<AGUIEvent>();
        try
        {
            var attachment = await _projectionPort.EnsureAndAttachLeaseAsync(
                token => _projectionPort.EnsureChatProjectionAsync(
                    target.ActorId,
                    sessionId,
                    token),
                sink,
                ct);
            if (attachment == null)
            {
                sink.Complete();
                await sink.DisposeAsync();
                return CommandTargetBindingResult<NyxIdChatStartError>.Failure(NyxIdChatStartError.ProjectionUnavailable);
            }

            target.BindLiveObservation(attachment.ProjectionLease, attachment.LiveSinkLease, sink, sessionId);
            return CommandTargetBindingResult<NyxIdChatStartError>.Success();
        }
        catch
        {
            sink.Complete();
            await sink.DisposeAsync();
            throw;
        }
    }
}

internal sealed class NyxIdChatCommandEnvelopeFactory : ICommandEnvelopeFactory<NyxIdChatCommand>
{
    public EventEnvelope CreateEnvelope(NyxIdChatCommand command, CommandContext context)
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
        if (command.InputParts is { Count: > 0 })
        {
            foreach (var part in command.InputParts)
                chatRequest.InputParts.Add(part.ToProto());
        }

        chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = command.AccessToken;
        chatRequest.Metadata["scope_id"] = command.ScopeId;
        AppendMetadata(chatRequest.Metadata, command.Metadata);

        return CreateDirectEnvelope(context, chatRequest);
    }

    private static void AppendMetadata(
        Google.Protobuf.Collections.MapField<string, string> destination,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source == null)
            return;

        foreach (var (key, value) in source)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                destination[key.Trim()] = value.Trim();
        }
    }

    private static EventEnvelope CreateDirectEnvelope(CommandContext context, IMessage message) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(message),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = context.TargetId } },
            Propagation = new EnvelopePropagation { CorrelationId = context.CorrelationId },
        };
}

internal sealed class NyxIdApprovalCommandEnvelopeFactory : ICommandEnvelopeFactory<NyxIdApprovalCommand>
{
    public EventEnvelope CreateEnvelope(NyxIdApprovalCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new ToolApprovalDecisionEvent
            {
                RequestId = command.RequestId,
                SessionId = command.SessionId,
                Approved = command.Approved,
                Reason = command.Reason,
            }),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = context.TargetId } },
            Propagation = new EnvelopePropagation { CorrelationId = context.CorrelationId },
        };
    }
}

internal sealed class NyxIdChatAcceptedReceiptFactory
    : ICommandReceiptFactory<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>
{
    public NyxIdChatAcceptedReceipt Create(
        NyxIdChatCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new NyxIdChatAcceptedReceipt(
            target.ActorId,
            context.CommandId,
            context.CorrelationId,
            target.SessionId);
    }
}

internal sealed class NyxIdChatCompletionPolicy
    : ICommandCompletionPolicy<AGUIEvent, NyxIdChatCompletionStatus>
{
    public NyxIdChatCompletionStatus IncompleteCompletion => NyxIdChatCompletionStatus.Unknown;

    public bool TryResolve(AGUIEvent evt, out NyxIdChatCompletionStatus completion)
    {
        ArgumentNullException.ThrowIfNull(evt);

        completion = evt.EventCase switch
        {
            AGUIEvent.EventOneofCase.RunFinished => NyxIdChatCompletionStatus.Completed,
            AGUIEvent.EventOneofCase.RunError => NyxIdChatCompletionStatus.Failed,
            _ => NyxIdChatCompletionStatus.Unknown,
        };
        return completion != NyxIdChatCompletionStatus.Unknown;
    }
}

internal sealed class NyxIdChatFinalizeEmitter
    : ICommandFinalizeEmitter<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus, AGUIEvent>
{
    public Task EmitAsync(
        NyxIdChatAcceptedReceipt receipt,
        NyxIdChatCompletionStatus completion,
        bool completed,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
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
            new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    Message = "Request timed out.",
                    Code = "timeout",
                },
            },
            ct).AsTask();
    }
}

internal sealed class NyxIdChatDurableCompletionResolver
    : ICommandDurableCompletionResolver<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus>
{
    public Task<CommandDurableCompletionObservation<NyxIdChatCompletionStatus>> ResolveAsync(
        NyxIdChatAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        _ = receipt;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CommandDurableCompletionObservation<NyxIdChatCompletionStatus>.Incomplete);
    }
}

internal static class NyxIdChatInteractionFactories
{
    public static ICommandTargetResolver<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatStartError> CreateChatResolver(
        IServiceProvider sp) =>
        new NyxIdChatCommandTargetResolver<NyxIdChatCommand>(
            sp.GetRequiredService<IActorRuntime>(),
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            static command => command.ActorId);

    public static ICommandTargetResolver<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatStartError> CreateApprovalResolver(
        IServiceProvider sp) =>
        new NyxIdChatCommandTargetResolver<NyxIdApprovalCommand>(
            sp.GetRequiredService<IActorRuntime>(),
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            static command => command.ActorId);

    public static ICommandTargetBinder<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatStartError> CreateChatBinder(
        IServiceProvider sp) =>
        new NyxIdChatCommandTargetBinder<NyxIdChatCommand>(
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            static command => command.SessionId);

    public static ICommandTargetBinder<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatStartError> CreateApprovalBinder(
        IServiceProvider sp) =>
        new NyxIdChatCommandTargetBinder<NyxIdApprovalCommand>(
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            static command => command.SessionId);
}
