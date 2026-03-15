using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using SystemType = System.Type;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class ProjectionDispatchCompensationOutboxGAgent
    : GAgentBase<ProjectionDispatchCompensationOutboxState>
{
    public const string ActorIdPrefix = "projection.compensation.outbox";
    private static readonly SystemType ProjectionWriteSinkContract = typeof(IProjectionWriteSink<>);
    private static readonly SystemType ProjectionReadModelContract = typeof(IProjectionReadModel);

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

        foreach (var entry in dueEntries)
            await ReplayEntryAsync(entry);
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
            ReadModel = evt.ReadModel?.Clone(),
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

    private async Task ReplayEntryAsync(ProjectionDispatchCompensationOutboxEntry entry)
    {
        if (!TryResolveReadModelType(entry.ReadModelType, out var readModelType))
        {
            await ScheduleRetryAsync(
                entry,
                new InvalidOperationException(
                    $"Compensation replay read model type '{entry.ReadModelType}' is not loadable."));
            return;
        }

        var binding = ResolveBinding(readModelType, entry.FailedStore);
        if (binding == null)
        {
            await ScheduleRetryAsync(
                entry,
                new InvalidOperationException(
                    $"Compensation replay binding '{entry.FailedStore}' is not registered for read model '{readModelType.FullName}'."));
            return;
        }

        if (!TryUnpackReadModel(entry.ReadModel, readModelType, out var readModel))
        {
            await ScheduleRetryAsync(
                entry,
                new InvalidOperationException(
                    $"Compensation replay payload is missing or incompatible for record '{entry.RecordId}'."));
            return;
        }

        try
        {
            _ = await InvokeUpsertAsync(binding, readModelType, readModel);
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

    private object? ResolveBinding(SystemType readModelType, string sinkName)
    {
        var sinkContract = ProjectionWriteSinkContract.MakeGenericType(readModelType);
        var sinkNameProperty = sinkContract.GetProperty(nameof(IProjectionWriteSink<IProjectionReadModel>.SinkName))
                              ?? throw new InvalidOperationException(
                                  $"Projection sink contract '{sinkContract.FullName}' does not expose SinkName.");
        var isEnabledProperty = sinkContract.GetProperty(nameof(IProjectionWriteSink<IProjectionReadModel>.IsEnabled))
                                ?? throw new InvalidOperationException(
                                    $"Projection sink contract '{sinkContract.FullName}' does not expose IsEnabled.");

        return Services.GetServices(sinkContract)
            .Cast<object>()
            .Where(binding => (bool)(isEnabledProperty.GetValue(binding) ?? false))
            .GroupBy(
                binding => (string?)sinkNameProperty.GetValue(binding) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase)
            .GetValueOrDefault(sinkName);
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
        var options = Services.GetService<IProjectionDispatchCompensationOptions>();
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

    private static bool TryResolveReadModelType(string? readModelTypeName, out SystemType readModelType)
    {
        readModelType = null!;
        if (string.IsNullOrWhiteSpace(readModelTypeName))
            return false;

        readModelType = SystemType.GetType(readModelTypeName, throwOnError: false) ??
                        AppDomain.CurrentDomain.GetAssemblies()
                            .Select(assembly => assembly.GetType(readModelTypeName, throwOnError: false, ignoreCase: false))
                            .FirstOrDefault(type => type != null)!;

        return readModelType != null &&
               ProjectionReadModelContract.IsAssignableFrom(readModelType) &&
               typeof(IMessage).IsAssignableFrom(readModelType);
    }

    private static bool TryUnpackReadModel(Any? payload, SystemType readModelType, out object readModel)
    {
        readModel = null!;
        if (payload == null)
            return false;

        if (Activator.CreateInstance(readModelType) is not IMessage prototype)
            return false;

        var descriptor = prototype.Descriptor;
        if (descriptor.Parser == null || !payload.Is(descriptor))
            return false;

        try
        {
            var parsed = descriptor.Parser.ParseFrom(payload.Value);
            if (parsed is not null && readModelType.IsInstanceOfType(parsed))
            {
                readModel = parsed;
                return true;
            }

            return false;
        }
        catch (InvalidProtocolBufferException)
        {
            readModel = null!;
            return false;
        }
    }

    private static Task<ProjectionWriteResult> InvokeUpsertAsync(object binding, SystemType readModelType, object readModel)
    {
        var method = typeof(ProjectionDispatchCompensationOutboxGAgent)
            .GetMethod(nameof(InvokeUpsertCoreAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                     ?? throw new InvalidOperationException("Compensation replay upsert bridge is missing.");
        return (Task<ProjectionWriteResult>)method.MakeGenericMethod(readModelType)
            .Invoke(null, [binding, readModel])!;
    }

    private static Task<ProjectionWriteResult> InvokeUpsertCoreAsync<TReadModel>(object binding, object readModel)
        where TReadModel : class, IProjectionReadModel
    {
        return ((IProjectionWriteSink<TReadModel>)binding).UpsertAsync((TReadModel)readModel);
    }
}
