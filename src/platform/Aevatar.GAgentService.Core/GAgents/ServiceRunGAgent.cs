using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

public sealed class ServiceRunGAgent : GAgentBase<ServiceRunState>
{
    public ServiceRunGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleRegisterAsync(RegisterServiceRunRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Record);
        ValidateRecord(command.Record);

        var existing = State.Record;
        if (existing != null && !string.IsNullOrWhiteSpace(existing.RunId))
        {
            if (!string.Equals(existing.RunId, command.Record.RunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Service run actor '{Id}' is bound to run '{existing.RunId}' and cannot register run '{command.Record.RunId}'.");
            }

            return;
        }

        var record = command.Record.Clone();
        if (record.CreatedAt == null)
            record.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        record.UpdatedAt = record.CreatedAt;
        if (record.Status == ServiceRunStatus.Unspecified)
            record.Status = ServiceRunStatus.Accepted;

        await PersistDomainEventAsync(new ServiceRunRegisteredEvent
        {
            Record = record,
        });
    }

    [EventHandler]
    public async Task HandleUpdateStatusAsync(UpdateServiceRunStatusRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = State.Record;
        if (existing == null || string.IsNullOrWhiteSpace(existing.RunId))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' has no registered run; status update rejected.");
        }

        if (!string.IsNullOrWhiteSpace(command.RunId) &&
            !string.Equals(existing.RunId, command.RunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' is bound to run '{existing.RunId}' and cannot update run '{command.RunId}'.");
        }

        if (command.Status == ServiceRunStatus.Unspecified)
            return;

        if (existing.Status == command.Status)
            return;

        await PersistDomainEventAsync(new ServiceRunStatusUpdatedEvent
        {
            RunId = existing.RunId,
            Status = command.Status,
            UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    protected override ServiceRunState TransitionState(ServiceRunState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceRunRegisteredEvent>(ApplyRegistered)
            .On<ServiceRunStatusUpdatedEvent>(ApplyStatusUpdated)
            .OrCurrent();

    private static ServiceRunState ApplyRegistered(ServiceRunState state, ServiceRunRegisteredEvent evt)
    {
        var next = state.Clone();
        next.Record = evt.Record?.Clone() ?? new ServiceRunRecord();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.RunId}:registered";
        return next;
    }

    private static ServiceRunState ApplyStatusUpdated(ServiceRunState state, ServiceRunStatusUpdatedEvent evt)
    {
        var next = state.Clone();
        if (next.Record == null)
            next.Record = new ServiceRunRecord();
        next.Record.Status = evt.Status;
        next.Record.UpdatedAt = evt.UpdatedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.RunId}:status:{(int)evt.Status}";
        return next;
    }

    private static void ValidateRecord(ServiceRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.RunId))
            throw new InvalidOperationException("run_id is required.");
        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (string.IsNullOrWhiteSpace(record.ServiceId))
            throw new InvalidOperationException("service_id is required.");
        if (string.IsNullOrWhiteSpace(record.CommandId))
            throw new InvalidOperationException("command_id is required.");
    }
}
