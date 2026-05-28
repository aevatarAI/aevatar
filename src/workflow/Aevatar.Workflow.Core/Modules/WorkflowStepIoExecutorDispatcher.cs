using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Modules;

internal static class WorkflowStepIoExecutorDispatcher
{
    // Refactor (iter110/cluster-1): Old pattern: modules blocked actor handling on connector/tool IO.  New principle: modules publish typed intents and this internal bounded dispatcher returns connector/tool-specific continuations.
    public static Task DispatchToolCallAsync(
        IWorkflowExecutionContext ctx,
        ToolCallIntentEvent intent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);
        var queue = ctx.Services.GetRequiredService<IWorkflowStepIoDispatchQueue>();
        return queue.EnqueueAsync(
            WorkflowStepIoWorkItem.Create(
                ctx.AgentId,
                ctx.InboundEnvelope,
                intent.Clone(),
                static (executor, typedIntent) =>
                    executor.ExecuteToolCallAsync(typedIntent, CancellationToken.None)),
            ct).AsTask();
    }

    // Refactor (iter110/cluster-1): Old pattern: connector_call retry and timeout executed inline in the module turn.  New principle: connector typed intent is handed to an executor and its typed continuation wakes the actor later.
    public static Task DispatchConnectorCallAsync(
        IWorkflowExecutionContext ctx,
        ConnectorCallIntentEvent intent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(intent);
        var queue = ctx.Services.GetRequiredService<IWorkflowStepIoDispatchQueue>();
        return queue.EnqueueAsync(
            WorkflowStepIoWorkItem.Create(
                ctx.AgentId,
                ctx.InboundEnvelope,
                intent.Clone(),
                static (executor, typedIntent) =>
                    executor.ExecuteConnectorCallAsync(typedIntent, CancellationToken.None)),
            ct).AsTask();
    }
}

internal interface IWorkflowStepIoDispatchQueue
{
    ValueTask EnqueueAsync(WorkflowStepIoWorkItem item, CancellationToken ct);
    IAsyncEnumerable<WorkflowStepIoWorkItem> DequeueAllAsync(CancellationToken ct);
}

internal sealed class WorkflowStepIoDispatchQueue : IWorkflowStepIoDispatchQueue, IDisposable
{
    private const int Capacity = 256;
    private const int WorkerCount = 4;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private int _started;
    private readonly Channel<WorkflowStepIoWorkItem> _channel = Channel.CreateBounded<WorkflowStepIoWorkItem>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

    public WorkflowStepIoDispatchQueue(
        IServiceProvider services,
        ILogger<WorkflowStepIoDispatchQueue>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? NullLogger<WorkflowStepIoDispatchQueue>.Instance;
    }

    public ValueTask EnqueueAsync(WorkflowStepIoWorkItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureStarted();
        return _channel.Writer.WriteAsync(item, ct);
    }

    public IAsyncEnumerable<WorkflowStepIoWorkItem> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        for (var i = 0; i < WorkerCount; i++)
            _ = Task.Run(() => ProcessQueueAsync(_shutdown.Token));
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in DequeueAllAsync(stoppingToken))
        {
            try
            {
                var executor = _services.GetRequiredService<IWorkflowStepIoExecutor>();
                var dispatchPort = _services.GetRequiredService<IActorDispatchPort>();
                var continuation = await item.ExecuteAsync(executor);
                var envelope = WorkflowStepIoContinuationEnvelopeFactory.Create(item, continuation);
                await dispatchPort.DispatchAsync(item.TargetActorId, envelope, CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Workflow step IO worker failed. targetActorId={TargetActorId} intent={IntentType}",
                    item.TargetActorId,
                    item.Intent.Descriptor.FullName);
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}

internal sealed class WorkflowStepIoWorker : BackgroundService
{
    private readonly IWorkflowStepIoDispatchQueue _queue;
    private readonly ILogger<WorkflowStepIoWorker> _logger;

    public WorkflowStepIoWorker(
        IWorkflowStepIoDispatchQueue queue,
        ILogger<WorkflowStepIoWorker> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_queue is WorkflowStepIoDispatchQueue queue)
            queue.EnsureStarted();
        else
            _logger.LogDebug("Workflow step IO dispatch queue is provided by {QueueType}", _queue.GetType().Name);
        return Task.CompletedTask;
    }
}

internal static class WorkflowStepIoContinuationEnvelopeFactory
{
    public static EventEnvelope Create(
        WorkflowStepIoWorkItem item,
        IMessage continuation)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(continuation),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(item.TargetActorId, TopologyAudience.Self),
        };

        if (item.SourceEnvelope.Propagation != null)
            envelope.Propagation = item.SourceEnvelope.Propagation.Clone();

        return envelope;
    }
}

internal sealed class WorkflowStepIoWorkItem
{
    private readonly Func<IWorkflowStepIoExecutor, Task<IMessage>> _execute;

    private WorkflowStepIoWorkItem(
        string targetActorId,
        EventEnvelope sourceEnvelope,
        IMessage intent,
        Func<IWorkflowStepIoExecutor, Task<IMessage>> execute)
    {
        TargetActorId = targetActorId;
        SourceEnvelope = sourceEnvelope;
        Intent = intent;
        _execute = execute;
    }

    public string TargetActorId { get; }
    public EventEnvelope SourceEnvelope { get; }
    public IMessage Intent { get; }

    public static WorkflowStepIoWorkItem Create<TIntent, TContinuation>(
        string targetActorId,
        EventEnvelope sourceEnvelope,
        TIntent intent,
        Func<IWorkflowStepIoExecutor, TIntent, Task<TContinuation>> execute)
        where TIntent : class, IMessage<TIntent>
        where TContinuation : IMessage
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(sourceEnvelope);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(execute);

        return new WorkflowStepIoWorkItem(
            targetActorId.Trim(),
            sourceEnvelope.Clone(),
            intent.Clone(),
            async executor => await execute(executor, intent.Clone()));
    }

    public Task<IMessage> ExecuteAsync(IWorkflowStepIoExecutor executor) => _execute(executor);
}
