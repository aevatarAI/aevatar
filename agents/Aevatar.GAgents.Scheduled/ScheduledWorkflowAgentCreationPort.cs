using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.GAgents.Scheduled;

public interface IScheduledWorkflowAgentCreationPort
{
    Task<ScheduledWorkflowAgentCreationReceipt> CreateAsync(
        ScheduledWorkflowAgentCreateRequest request,
        CancellationToken ct = default);
}

public sealed class ScheduledWorkflowAgentCreationPort : IScheduledWorkflowAgentCreationPort
{
    private readonly IWorkflowScheduleCommandPort _workflowSchedules;
    private readonly IScheduledDispatchActorPort _scheduleActorPort;
    private readonly IUserAgentCatalogCommandPort _catalogCommandPort;

    public ScheduledWorkflowAgentCreationPort(
        IWorkflowScheduleCommandPort workflowSchedules,
        IScheduledDispatchActorPort scheduleActorPort,
        IUserAgentCatalogCommandPort catalogCommandPort)
    {
        _workflowSchedules = workflowSchedules ?? throw new ArgumentNullException(nameof(workflowSchedules));
        _scheduleActorPort = scheduleActorPort ?? throw new ArgumentNullException(nameof(scheduleActorPort));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
    }

    public async Task<ScheduledWorkflowAgentCreationReceipt> CreateAsync(
        ScheduledWorkflowAgentCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _workflowSchedules.EnsureAsync(request.Schedule, ct);
        await _catalogCommandPort.UpsertAsync(request.CatalogEntry, ct);

        if (request.RunImmediately && receipt.Accepted)
            await _scheduleActorPort.DispatchRunNowAsync(receipt.ScheduleActorId, DateTimeOffset.UtcNow, ct);

        return new ScheduledWorkflowAgentCreationReceipt(
            receipt.ScheduleId,
            receipt.ScheduleActorId,
            receipt.Accepted,
            receipt.CommandId,
            receipt.CorrelationId,
            receipt.AckedAt,
            receipt.AckStage);
    }
}

public sealed record ScheduledWorkflowAgentCreateRequest(
    WorkflowScheduleConfiguration Schedule,
    UserAgentCatalogUpsertCommand CatalogEntry,
    bool RunImmediately);

public sealed record ScheduledWorkflowAgentCreationReceipt(
    string AgentId,
    string ScheduleActorId,
    bool Accepted,
    string CommandId,
    string CorrelationId,
    DateTimeOffset AckedAt,
    string AckStage);
