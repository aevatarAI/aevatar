using System.Text.Json;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowProjectionDispatchCompensationOutboxGAgent
    : GAgentBase<ProjectionDispatchCompensationOutboxState>
{
    public const string ActorIdPrefix = "projection.compensation.outbox";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string BuildActorId(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("Scope id is required.", nameof(scopeId));

        return $"{ActorIdPrefix}:{scopeId.Trim()}";
    }

    [EventHandler]
    public async Task HandleEnqueueAsync(ProjectionCompensationEnqueuedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RecordId))
            throw new InvalidOperationException("Record id is required to enqueue compensation.");
        if (string.IsNullOrWhiteSpace(evt.FailedStore))
            throw new InvalidOperationException("Failed store is required to enqueue compensation.");

        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleTriggerReplayAsync(ProjectionCompensationTriggerReplayEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var utcNow = DateTime.UtcNow;
        var batchSize = evt.BatchSize > 0 ? evt.BatchSize : 20;

        var dueEntries = State.Entries.Values
            .Where(e => e.CompletedAtUtc == null && IsVisible(e, utcNow))
            .OrderBy(e => e.NextVisibleAtUtc?.ToDateTime() ?? DateTime.UnixEpoch)
            .ThenBy(e => e.EnqueuedAtUtc?.ToDateTime() ?? DateTime.UnixEpoch)
            .Take(batchSize)
            .ToList();

        var bindings = Services
            .GetServices<IProjectionStoreBinding<WorkflowExecutionReport, string>>()
            .GroupBy(b => b.StoreName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in dueEntries)
        {
            await ReplayEntryAsync(entry, bindings);
        }
    }

    protected override ProjectionDispatchCompensationOutboxState TransitionState(
        ProjectionDispatchCompensationOutboxState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ProjectionCompensationEnqueuedEvent>(ApplyEnqueued)
            .On<ProjectionCompensationRetryScheduledEvent>(ApplyRetryScheduled)
            .On<ProjectionCompensationSucceededEvent>(ApplySucceeded)
            .OrCurrent();

    private static ProjectionDispatchCompensationOutboxState ApplyEnqueued(
        ProjectionDispatchCompensationOutboxState current,
        ProjectionCompensationEnqueuedEvent evt)
    {
        var next = current.Clone();
        next.Entries[evt.RecordId] = new ProjectionDispatchCompensationOutboxEntry
        {
            RecordId = evt.RecordId,
            Operation = evt.Operation,
            FailedStore = evt.FailedStore,
            SucceededStores = { evt.SucceededStores },
            ReadModelType = evt.ReadModelType,
            ReadModelJson = evt.ReadModelJson,
            Key = evt.Key,
            EnqueuedAtUtc = evt.EnqueuedAtUtc,
            NextVisibleAtUtc = evt.EnqueuedAtUtc,
            AttemptCount = 0,
            LastError = evt.LastError,
        };
        return next;
    }

    private static ProjectionDispatchCompensationOutboxState ApplyRetryScheduled(
        ProjectionDispatchCompensationOutboxState current,
        ProjectionCompensationRetryScheduledEvent evt)
    {
        var next = current.Clone();
        if (!next.Entries.TryGetValue(evt.RecordId, out var existing))
            return next;

        var updated = existing.Clone();
        updated.AttemptCount = Math.Max(1, evt.AttemptCount);
        updated.NextVisibleAtUtc = evt.NextVisibleAtUtc;
        updated.LastError = evt.Error;
        next.Entries[evt.RecordId] = updated;
        return next;
    }

    private static ProjectionDispatchCompensationOutboxState ApplySucceeded(
        ProjectionDispatchCompensationOutboxState current,
        ProjectionCompensationSucceededEvent evt)
    {
        var next = current.Clone();
        if (!next.Entries.TryGetValue(evt.RecordId, out var existing))
            return next;

        var updated = existing.Clone();
        updated.CompletedAtUtc = evt.CompletedAtUtc;
        next.Entries[evt.RecordId] = updated;
        return next;
    }

    private async Task ReplayEntryAsync(
        ProjectionDispatchCompensationOutboxEntry entry,
        IReadOnlyDictionary<string, IProjectionStoreBinding<WorkflowExecutionReport, string>> bindings)
    {
        if (!bindings.TryGetValue(entry.FailedStore, out var binding))
        {
            await ScheduleRetryAsync(
                entry,
                new InvalidOperationException(
                    $"Compensation replay binding '{entry.FailedStore}' is not registered."));
            return;
        }

        WorkflowExecutionReport? readModel;
        try
        {
            readModel = JsonSerializer.Deserialize<WorkflowExecutionReport>(entry.ReadModelJson, JsonOptions);
        }
        catch (Exception ex)
        {
            await ScheduleRetryAsync(entry, ex);
            return;
        }

        if (readModel == null)
        {
            await ScheduleRetryAsync(
                entry,
                new InvalidOperationException(
                    $"Compensation replay deserialized null read model for record '{entry.RecordId}'."));
            return;
        }

        try
        {
            await binding.UpsertAsync(readModel);
            await PersistDomainEventAsync(new ProjectionCompensationSucceededEvent
            {
                RecordId = entry.RecordId,
                CompletedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }
        catch (Exception ex)
        {
            await ScheduleRetryAsync(entry, ex);
        }
    }

    private async Task ScheduleRetryAsync(
        ProjectionDispatchCompensationOutboxEntry entry,
        Exception exception)
    {
        var nextAttempt = entry.AttemptCount + 1;
        var delay = ComputeRetryDelay(nextAttempt);
        var nextVisibleAt = DateTime.UtcNow + delay;
        await PersistDomainEventAsync(new ProjectionCompensationRetryScheduledEvent
        {
            RecordId = entry.RecordId,
            AttemptCount = nextAttempt,
            NextVisibleAtUtc = Timestamp.FromDateTime(nextVisibleAt),
            Error = $"{exception.GetType().Name}: {exception.Message}",
        });
    }

    private TimeSpan ComputeRetryDelay(int attempt)
    {
        var options = Services.GetService<WorkflowExecutionProjectionOptions>();
        var baseDelayMs = Math.Max(0, options?.DispatchCompensationReplayBaseDelayMs ?? 1000);
        var maxDelayMs = Math.Max(baseDelayMs, options?.DispatchCompensationReplayMaxDelayMs ?? 60_000);
        if (baseDelayMs == 0)
            return TimeSpan.Zero;

        var exponent = Math.Min(10, Math.Max(0, attempt - 1));
        var nextDelay = (long)Math.Round(baseDelayMs * Math.Pow(2, exponent), MidpointRounding.AwayFromZero);
        return TimeSpan.FromMilliseconds(Math.Min(nextDelay, maxDelayMs));
    }

    private static bool IsVisible(ProjectionDispatchCompensationOutboxEntry entry, DateTime utcNow)
    {
        if (entry.NextVisibleAtUtc == null)
            return true;

        var nextVisible = entry.NextVisibleAtUtc.ToDateTime();
        if (nextVisible.Kind != DateTimeKind.Utc)
            nextVisible = DateTime.SpecifyKind(nextVisible, DateTimeKind.Utc);

        return utcNow >= nextVisible;
    }
}
