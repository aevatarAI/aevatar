using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

internal sealed class WorkflowStepIoExecutorDispatcher
{
    private readonly IServiceProvider _services;

    public WorkflowStepIoExecutorDispatcher(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    internal async Task ProcessOneAsync(
        string targetActorId,
        WorkflowRunState.Types.PendingIoWorkItem pending,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(pending);

        var item = WorkflowStepIoWorkItem.FromPending(targetActorId, pending);
        var executor = _services.GetRequiredService<IWorkflowStepIoExecutor>();
        var dispatchPort = _services.GetRequiredService<IActorDispatchPort>();
        IMessage continuation;
        try
        {
            continuation = await item.ExecuteAsync(executor, ct);
        }
        catch (Exception ex)
        {
            continuation = WorkflowStepIoContinuationEnvelopeFactory.CreateFailureContinuation(item, ex);
        }

        var envelope = WorkflowStepIoContinuationEnvelopeFactory.Create(item, continuation);
        await dispatchPort.DispatchAsync(item.TargetActorId, envelope, ct);
    }
}

internal sealed class WorkflowStepIoWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WorkflowStepIoWorker> _logger;

    public WorkflowStepIoWorker(
        IServiceProvider services,
        ILogger<WorkflowStepIoWorker> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ScanPendingItemsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workflow step IO worker scan failed.");
            }
        }
    }

    internal async Task ScanPendingItemsAsync(CancellationToken ct = default)
    {
        var queryPort = _services.GetService<IWorkflowExecutionCurrentStateQueryPort>();
        if (queryPort == null)
        {
            _logger.LogDebug("Workflow step IO worker skipped because no workflow current-state query port is registered.");
            return;
        }

        var snapshots = await queryPort.ListActorSnapshotsAsync(1000, ct);
        foreach (var snapshot in snapshots.Where(x => x.PendingIoWorkItemCount > 0))
            await ProcessActorPendingItemsAsync(snapshot.ActorId, ct);
    }

    internal async Task ProcessActorPendingItemsAsync(string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return;

        var runtime = _services.GetService<IActorRuntime>();
        if (runtime == null)
        {
            _logger.LogDebug("Workflow step IO worker skipped actor {ActorId} because no actor runtime is registered.", actorId);
            return;
        }

        var actor = await runtime.GetAsync(actorId.Trim());
        if (actor?.Agent is not IAgent<WorkflowRunState> workflowRun)
            return;

        var dispatcher = _services.GetRequiredService<WorkflowStepIoExecutorDispatcher>();
        var pendingItems = workflowRun.State.PendingIoWorkItems
            .Select(x => x.Clone())
            .ToList();
        foreach (var pending in pendingItems)
        {
            try
            {
                await dispatcher.ProcessOneAsync(actorId, pending, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Workflow step IO worker failed. targetActorId={TargetActorId} workItemId={WorkItemId}",
                    actorId,
                    pending.WorkItemId);
            }
        }
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

        if (item.SourcePropagation != null)
            envelope.Propagation = item.SourcePropagation.Clone();

        return envelope;
    }

    public static IMessage CreateFailureContinuation(
        WorkflowStepIoWorkItem item,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(exception);
        return item.Intent switch
        {
            ToolCallIntentEvent intent => new ToolCallContinuationResultEvent
            {
                StepId = intent.StepId,
                RunId = intent.RunId,
                ExecutionId = intent.ExecutionId,
                ToolName = intent.ToolName,
                Success = false,
                Error = exception.Message,
            },
            ConnectorCallIntentEvent intent => new ConnectorCallContinuationResultEvent
            {
                StepId = intent.StepId,
                RunId = intent.RunId,
                ExecutionId = intent.ExecutionId,
                ConnectorName = intent.ConnectorName,
                Operation = intent.Operation,
                Success = false,
                Error = exception.Message,
                TimeoutMs = intent.TimeoutMs,
            },
            _ => throw new InvalidOperationException(
                $"Unsupported workflow step IO intent '{item.Intent.Descriptor.FullName}'."),
        };
    }
}

internal sealed class WorkflowStepIoWorkItem
{
    private readonly Func<IWorkflowStepIoExecutor, CancellationToken, Task<IMessage>> _execute;

    private WorkflowStepIoWorkItem(
        string targetActorId,
        string workItemId,
        EnvelopePropagation? sourcePropagation,
        IMessage intent,
        Func<IWorkflowStepIoExecutor, CancellationToken, Task<IMessage>> execute)
    {
        TargetActorId = targetActorId;
        WorkItemId = workItemId;
        SourcePropagation = sourcePropagation;
        Intent = intent;
        _execute = execute;
    }

    public string TargetActorId { get; }
    public string WorkItemId { get; }
    public EnvelopePropagation? SourcePropagation { get; }
    public IMessage Intent { get; }

    public static WorkflowStepIoWorkItem FromPending(
        string targetActorId,
        WorkflowRunState.Types.PendingIoWorkItem pending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(pending);

        return pending.IntentCase switch
        {
            WorkflowRunState.Types.PendingIoWorkItem.IntentOneofCase.ToolIntent => Create(
                targetActorId,
                pending.WorkItemId,
                pending.SourcePropagation,
                pending.ToolIntent,
                static (executor, typedIntent, token) =>
                    executor.ExecuteToolCallAsync(typedIntent, token)),
            WorkflowRunState.Types.PendingIoWorkItem.IntentOneofCase.ConnectorIntent => Create(
                targetActorId,
                pending.WorkItemId,
                pending.SourcePropagation,
                pending.ConnectorIntent,
                static (executor, typedIntent, token) =>
                    executor.ExecuteConnectorCallAsync(typedIntent, token)),
            _ => throw new InvalidOperationException(
                $"Workflow step IO work item '{pending.WorkItemId}' has no intent."),
        };
    }

    private static WorkflowStepIoWorkItem Create<TIntent, TContinuation>(
        string targetActorId,
        string workItemId,
        EnvelopePropagation? sourcePropagation,
        TIntent intent,
        Func<IWorkflowStepIoExecutor, TIntent, CancellationToken, Task<TContinuation>> execute)
        where TIntent : class, IMessage<TIntent>
        where TContinuation : IMessage
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(execute);

        return new WorkflowStepIoWorkItem(
            targetActorId.Trim(),
            workItemId?.Trim() ?? string.Empty,
            sourcePropagation?.Clone(),
            intent.Clone(),
            async (executor, token) => await execute(executor, intent.Clone(), token));
    }

    public Task<IMessage> ExecuteAsync(IWorkflowStepIoExecutor executor, CancellationToken ct) => _execute(executor, ct);
}
