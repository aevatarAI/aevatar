using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.Workflow.Application.Schedules;

public sealed class WorkflowScheduleApplicationService : IWorkflowScheduleApplicationService
{
    private const int DefaultPreviewCount = 10;
    private const int MaxListTake = 500;

    private readonly IWorkflowScheduleStore _store;
    private readonly IWorkflowScheduleWakeupScheduler _wakeupScheduler;
    private readonly ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _dispatchService;
    private readonly TimeProvider _clock;

    public WorkflowScheduleApplicationService(
        IWorkflowScheduleStore store,
        IWorkflowScheduleWakeupScheduler wakeupScheduler,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> dispatchService,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _wakeupScheduler = wakeupScheduler ?? throw new ArgumentNullException(nameof(wakeupScheduler));
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> CreateAsync(
        WorkflowScheduleCreateCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalizedId = NormalizeScheduleId(command.ScheduleId) ?? Guid.NewGuid().ToString("N");
        var validation = ValidateDefinitionInputs(
            normalizedId,
            command.Name,
            command.Cron,
            command.Timezone,
            command.Target);
        if (validation != null)
            return WorkflowScheduleResult<WorkflowScheduleDefinition>.Failure(validation.Code, validation.Message);

        if (await _store.GetAsync(normalizedId, ct) != null)
        {
            return WorkflowScheduleResult<WorkflowScheduleDefinition>.Failure(
                WorkflowScheduleErrorCode.AlreadyExists,
                $"Schedule '{normalizedId}' already exists.");
        }

        var now = _clock.GetUtcNow();
        var status = command.Enabled
            ? WorkflowScheduleStatus.Enabled
            : WorkflowScheduleStatus.Disabled;
        var next = status == WorkflowScheduleStatus.Enabled
            ? ComputeNextFireOrFailure(command.Cron, command.Timezone, now)
            : WorkflowScheduleResult<DateTimeOffset?>.Success(null);
        if (!next.Succeeded)
            return WorkflowScheduleResult<WorkflowScheduleDefinition>.Failure(next.Error.Code, next.Error.Message);

        var definition = new WorkflowScheduleDefinition(
            normalizedId,
            command.Name.Trim(),
            command.Cron.Trim(),
            NormalizeTimezone(command.Timezone),
            status,
            command.Target,
            now,
            now,
            next.Value);

        await _store.AddAsync(definition, ct);
        await ScheduleWakeupAsync(definition, ct);
        return WorkflowScheduleResult<WorkflowScheduleDefinition>.Success(definition);
    }

    public async Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> UpdateAsync(
        string scheduleId,
        WorkflowScheduleUpdateCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = await GetExistingAsync(scheduleId, ct);
        if (!existing.Succeeded)
            return existing;

        var current = existing.Value!;
        var name = string.IsNullOrWhiteSpace(command.Name) ? current.Name : command.Name.Trim();
        var cron = string.IsNullOrWhiteSpace(command.Cron) ? current.Cron : command.Cron.Trim();
        var timezone = string.IsNullOrWhiteSpace(command.Timezone) ? current.Timezone : NormalizeTimezone(command.Timezone);
        var target = command.Target ?? current.Target;
        var validation = ValidateDefinitionInputs(current.ScheduleId, name, cron, timezone, target);
        if (validation != null)
            return WorkflowScheduleResult<WorkflowScheduleDefinition>.Failure(validation.Code, validation.Message);

        var next = current.Status == WorkflowScheduleStatus.Enabled
            ? ComputeNextFireOrFailure(cron, timezone, _clock.GetUtcNow())
            : WorkflowScheduleResult<DateTimeOffset?>.Success(null);
        if (!next.Succeeded)
            return WorkflowScheduleResult<WorkflowScheduleDefinition>.Failure(next.Error.Code, next.Error.Message);

        var updated = current with
        {
            Name = name,
            Cron = cron,
            Timezone = timezone,
            Target = target,
            UpdatedAtUtc = _clock.GetUtcNow(),
            NextFireAtUtc = next.Value,
        };

        await _store.UpdateAsync(updated, ct);
        await ScheduleWakeupAsync(updated, ct);
        return WorkflowScheduleResult<WorkflowScheduleDefinition>.Success(updated);
    }

    public async Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> EnableAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var existing = await GetExistingAsync(scheduleId, ct);
        if (!existing.Succeeded)
            return existing;

        var current = existing.Value!;
        var next = ComputeNextFireOrFailure(current.Cron, current.Timezone, _clock.GetUtcNow());
        if (!next.Succeeded)
            return WorkflowScheduleResult<WorkflowScheduleDefinition>.Failure(next.Error.Code, next.Error.Message);

        var updated = current with
        {
            Status = WorkflowScheduleStatus.Enabled,
            UpdatedAtUtc = _clock.GetUtcNow(),
            NextFireAtUtc = next.Value,
        };
        await _store.UpdateAsync(updated, ct);
        await _wakeupScheduler.ScheduleAsync(updated, ct);
        return WorkflowScheduleResult<WorkflowScheduleDefinition>.Success(updated);
    }

    public async Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> DisableAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var existing = await GetExistingAsync(scheduleId, ct);
        if (!existing.Succeeded)
            return existing;

        var updated = existing.Value! with
        {
            Status = WorkflowScheduleStatus.Disabled,
            UpdatedAtUtc = _clock.GetUtcNow(),
            NextFireAtUtc = null,
        };
        await _store.UpdateAsync(updated, ct);
        await _wakeupScheduler.CancelAsync(updated.ScheduleId, ct);
        return WorkflowScheduleResult<WorkflowScheduleDefinition>.Success(updated);
    }

    public Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> GetAsync(
        string scheduleId,
        CancellationToken ct = default) =>
        GetExistingAsync(scheduleId, ct);

    public async Task<WorkflowScheduleListResult> ListAsync(
        WorkflowScheduleListQuery query,
        CancellationToken ct = default)
    {
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take <= 0 ? 100 : query.Take, 1, MaxListTake);
        var all = await _store.ListAsync(ct);
        var filtered = all
            .Where(x => query.Status == null || x.Status == query.Status)
            .OrderBy(x => x.ScheduleId, StringComparer.Ordinal)
            .ToList();

        return new WorkflowScheduleListResult(
            filtered.Skip(skip).Take(take).ToList(),
            filtered.Count);
    }

    public Task<WorkflowScheduleResult<WorkflowSchedulePreview>> PreviewAsync(
        string cron,
        string timezone,
        DateTimeOffset? fromUtc,
        int count,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var start = (fromUtc ?? _clock.GetUtcNow()).ToUniversalTime();
        var boundedCount = count <= 0 ? DefaultPreviewCount : count;
        var result = WorkflowScheduleCalculator.GetNextFireTimes(cron, timezone, start, boundedCount);
        if (!result.Succeeded)
        {
            return Task.FromResult(WorkflowScheduleResult<WorkflowSchedulePreview>.Failure(
                result.Error.Code,
                result.Error.Message));
        }

        return Task.FromResult(WorkflowScheduleResult<WorkflowSchedulePreview>.Success(
            new WorkflowSchedulePreview(
                cron.Trim(),
                NormalizeTimezone(timezone),
                start,
                result.Value ?? [])));
    }

    public async Task<WorkflowScheduleResult<WorkflowScheduleFireResult>> RunNowAsync(
        WorkflowScheduleFireRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await GetExistingAsync(request.ScheduleId, ct);
        if (!existing.Succeeded)
        {
            return WorkflowScheduleResult<WorkflowScheduleFireResult>.Failure(
                existing.Error.Code,
                existing.Error.Message);
        }

        var definition = existing.Value!;
        if (definition.Status != WorkflowScheduleStatus.Enabled && !request.Force)
        {
            return WorkflowScheduleResult<WorkflowScheduleFireResult>.Failure(
                WorkflowScheduleErrorCode.Disabled,
                $"Schedule '{definition.ScheduleId}' is disabled.");
        }

        var scheduledFireAtUtc = (request.ScheduledFireAtUtc ?? _clock.GetUtcNow()).ToUniversalTime();
        var idempotencyKey = WorkflowScheduleCalculator.BuildIdempotencyKey(definition.ScheduleId, scheduledFireAtUtc);
        var duplicate = await _store.GetRunAsync(idempotencyKey, ct);
        if (duplicate != null)
        {
            if (request.AdvanceSchedule)
                await AdvanceScheduleAfterFireAsync(definition, scheduledFireAtUtc, ct);

            return WorkflowScheduleResult<WorkflowScheduleFireResult>.Success(
                new WorkflowScheduleFireResult(WorkflowScheduleFireStatus.Duplicate, duplicate));
        }

        var run = new WorkflowScheduleRunRecord(
            RunRecordId: Guid.NewGuid().ToString("N"),
            ScheduleId: definition.ScheduleId,
            ScheduledFireAtUtc: scheduledFireAtUtc,
            FiredAtUtc: _clock.GetUtcNow(),
            IdempotencyKey: idempotencyKey,
            Status: WorkflowScheduleFireStatus.Rejected);
        await _store.AddRunAsync(run, ct);

        var dispatch = await _dispatchService.DispatchAsync(
            BuildRunRequest(definition, idempotencyKey),
            ct);

        if (!dispatch.Succeeded || dispatch.Receipt == null)
        {
            run = run with
            {
                Status = WorkflowScheduleFireStatus.Rejected,
                Error = dispatch.Error.ToString(),
            };
            await _store.UpdateRunAsync(run, ct);
            if (request.AdvanceSchedule)
                await AdvanceScheduleAfterFireAsync(definition, scheduledFireAtUtc, ct);

            return WorkflowScheduleResult<WorkflowScheduleFireResult>.Failure(
                WorkflowScheduleErrorCode.DispatchRejected,
                $"Schedule '{definition.ScheduleId}' dispatch was rejected: {dispatch.Error}.");
        }

        var receipt = dispatch.Receipt;
        run = run with
        {
            Status = WorkflowScheduleFireStatus.Accepted,
            AcceptedCommandId = receipt.CommandId,
            CorrelationId = receipt.CorrelationId,
            ActorId = receipt.ActorId,
        };
        await _store.UpdateRunAsync(run, ct);

        if (request.AdvanceSchedule)
            await AdvanceScheduleAfterFireAsync(definition, scheduledFireAtUtc, ct);

        return WorkflowScheduleResult<WorkflowScheduleFireResult>.Success(
            new WorkflowScheduleFireResult(
                WorkflowScheduleFireStatus.Accepted,
                run,
                $"/api/actors/{Uri.EscapeDataString(receipt.ActorId)}"));
    }

    private async Task<WorkflowScheduleResult<WorkflowScheduleDefinition>> GetExistingAsync(
        string scheduleId,
        CancellationToken ct)
    {
        var normalized = NormalizeScheduleId(scheduleId);
        if (normalized == null)
        {
            return WorkflowScheduleResult<WorkflowScheduleDefinition>.Failure(
                WorkflowScheduleErrorCode.InvalidScheduleId,
                "Schedule id is required.");
        }

        var definition = await _store.GetAsync(normalized, ct);
        if (definition == null)
        {
            return WorkflowScheduleResult<WorkflowScheduleDefinition>.Failure(
                WorkflowScheduleErrorCode.NotFound,
                $"Schedule '{normalized}' was not found.");
        }

        return WorkflowScheduleResult<WorkflowScheduleDefinition>.Success(definition);
    }

    private async Task ScheduleWakeupAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct)
    {
        if (definition.Status != WorkflowScheduleStatus.Enabled || definition.NextFireAtUtc == null)
        {
            await _wakeupScheduler.CancelAsync(definition.ScheduleId, ct);
            return;
        }

        await _wakeupScheduler.ScheduleAsync(definition, ct);
    }

    private async Task AdvanceScheduleAfterFireAsync(
        WorkflowScheduleDefinition definition,
        DateTimeOffset scheduledFireAtUtc,
        CancellationToken ct)
    {
        if (definition.Status != WorkflowScheduleStatus.Enabled)
            return;

        var nextResult = ComputeNextFireOrFailure(definition.Cron, definition.Timezone, scheduledFireAtUtc);
        if (!nextResult.Succeeded)
            return;

        var updated = definition with
        {
            UpdatedAtUtc = _clock.GetUtcNow(),
            NextFireAtUtc = nextResult.Value,
        };
        await _store.UpdateAsync(updated, ct);
        await ScheduleWakeupAsync(updated, ct);
    }

    private WorkflowScheduleResult<DateTimeOffset?> ComputeNextFireOrFailure(
        string cron,
        string timezone,
        DateTimeOffset fromUtc) =>
        WorkflowScheduleCalculator.GetNextFireTime(cron, timezone, fromUtc.ToUniversalTime());

    private static WorkflowChatRunRequest BuildRunRequest(
        WorkflowScheduleDefinition definition,
        string idempotencyKey)
    {
        var target = definition.Target;
        return new WorkflowChatRunRequest(
            Prompt: target.Prompt,
            WorkflowName: target.Source.WorkflowName,
            ActorId: target.Source.ActorId,
            SessionId: target.SessionId,
            InputParts: target.InputParts,
            WorkflowYamls: target.Source.WorkflowYamls,
            Metadata: target.Annotations,
            ScopeId: target.ScopeId,
            Source: target.Source,
            Headers: target.Headers,
            CommandIdSeed: idempotencyKey,
            CorrelationIdSeed: idempotencyKey);
    }

    private static WorkflowScheduleError? ValidateDefinitionInputs(
        string scheduleId,
        string name,
        string cron,
        string timezone,
        WorkflowScheduleTarget? target)
    {
        if (NormalizeScheduleId(scheduleId) == null)
            return new WorkflowScheduleError(WorkflowScheduleErrorCode.InvalidScheduleId, "Schedule id is required.");
        if (string.IsNullOrWhiteSpace(name))
            return new WorkflowScheduleError(WorkflowScheduleErrorCode.InvalidName, "Schedule name is required.");
        if (target == null || string.IsNullOrWhiteSpace(target.Prompt))
            return new WorkflowScheduleError(WorkflowScheduleErrorCode.InvalidTarget, "Schedule target prompt is required.");

        var preview = WorkflowScheduleCalculator.GetNextFireTimes(cron, timezone, DateTimeOffset.UtcNow, 1);
        if (!preview.Succeeded)
            return preview.Error;

        return null;
    }

    private static string? NormalizeScheduleId(string? scheduleId)
    {
        var normalized = scheduleId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return normalized.All(IsScheduleIdChar)
            ? normalized
            : null;
    }

    private static bool IsScheduleIdChar(char value) =>
        char.IsLetterOrDigit(value) || value is '-' or '_' or ':' or '.';

    private static string NormalizeTimezone(string? timezone) =>
        string.IsNullOrWhiteSpace(timezone)
            ? "UTC"
            : timezone.Trim();
}
