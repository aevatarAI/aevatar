using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;

namespace Aevatar.Capabilities.ExecutionActivity;

public sealed class ExecutionActivityPublisherHook : IGAgentExecutionHook
{
    private static readonly BoundedChannelOptions QueueOptions = new(1024)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    };

    private readonly IStreamProvider _streamProvider;
    private readonly IExecutionActivityScopeResolver _scopeResolver;
    private readonly ILogger<ExecutionActivityPublisherHook> _logger;
    private readonly Channel<QueuedExecutionActivityEvent> _queue = Channel.CreateBounded<QueuedExecutionActivityEvent>(QueueOptions);
    private readonly Task _drainTask;

    public ExecutionActivityPublisherHook(
        IStreamProvider streamProvider,
        IExecutionActivityScopeResolver scopeResolver,
        ILogger<ExecutionActivityPublisherHook>? logger = null)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
        _scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
        _logger = logger ?? NullLogger<ExecutionActivityPublisherHook>.Instance;
        _drainTask = Task.Run(DrainAsync);
    }

    public string Name => "execution_activity_publisher";
    public int Priority => -900;

    public Task OnEventHandlerStartAsync(GAgentExecutionHookContext ctx, CancellationToken ct) =>
        PublishBestEffortAsync(ctx, ExecutionActivityLifecycleStage.Started);

    public Task OnEventHandlerEndAsync(GAgentExecutionHookContext ctx, CancellationToken ct)
    {
        var stage = ctx.Exception == null
            ? ExecutionActivityLifecycleStage.Completed
            : ExecutionActivityLifecycleStage.Failed;
        return PublishBestEffortAsync(ctx, stage);
    }

    public Task OnErrorAsync(GAgentExecutionHookContext ctx, Exception ex, CancellationToken ct)
    {
        _ = ex;
        return Task.CompletedTask;
    }

    private Task PublishBestEffortAsync(
        GAgentExecutionHookContext ctx,
        ExecutionActivityLifecycleStage stage)
    {
        if (ResolveScopeId(ctx) is not { } scopeId)
            return Task.CompletedTask;

        var evt = new ExecutionActivityEvent
        {
            ScopeId = scopeId,
            ActorId = ctx.AgentId ?? string.Empty,
            AgentType = ctx.AgentType ?? string.Empty,
            HandlerName = ctx.HandlerName ?? string.Empty,
            EventType = ctx.EventType ?? string.Empty,
            EventId = ctx.EventId ?? string.Empty,
            Stage = stage,
            Ts = Timestamp.FromDateTime(DateTime.UtcNow),
            Error = stage == ExecutionActivityLifecycleStage.Failed
                ? ctx.Exception?.Message ?? string.Empty
                : string.Empty,
        };

        if (ctx.Duration is { } duration)
            evt.Duration = Duration.FromTimeSpan(duration);

        var queued = new QueuedExecutionActivityEvent(
            ExecutionActivityStreamTopics.ForScope(scopeId),
            evt);
        if (!_queue.Writer.TryWrite(queued))
        {
            _logger.LogDebug(
                "Execution activity queue dropped event. scopeId={ScopeId} actorId={ActorId} handler={HandlerName} stage={Stage}",
                scopeId,
                ctx.AgentId,
                ctx.HandlerName,
                stage);
        }

        return Task.CompletedTask;
    }

    private string? ResolveScopeId(GAgentExecutionHookContext ctx)
    {
        if (!ctx.Items.TryGetValue(GAgentExecutionHookItemKeys.InboundEnvelope, out var envelopeObj) ||
            envelopeObj is not EventEnvelope envelope)
            return null;

        return _scopeResolver.Resolve(envelope);
    }

    private async Task DrainAsync()
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await _streamProvider.GetStream(queued.StreamId).ProduceAsync(queued.Event);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Execution activity publish failed. streamId={StreamId} actorId={ActorId} handler={HandlerName} stage={Stage}",
                    queued.StreamId,
                    queued.Event.ActorId,
                    queued.Event.HandlerName,
                    queued.Event.Stage);
            }
        }
    }

    private sealed record QueuedExecutionActivityEvent(
        string StreamId,
        ExecutionActivityEvent Event);
}
