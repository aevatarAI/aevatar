using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Google.Protobuf.WellKnownTypes;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aevatar.AppPlatform.Infrastructure.Stores;

public sealed class InMemoryOperationStore : IAppOperationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AppOperationRecord> _records = new(StringComparer.Ordinal);

    public Task<AppOperationSnapshot> AcceptAsync(
        AppOperationSnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        var normalizedSnapshot = snapshot.Clone();
        normalizedSnapshot.Status = normalizedSnapshot.Status == AppOperationStatus.Unspecified
            ? AppOperationStatus.Accepted
            : normalizedSnapshot.Status;
        normalizedSnapshot.CreatedAt ??= UtcNowTimestamp();
        normalizedSnapshot.OperationId = NormalizeOperationId(normalizedSnapshot.OperationId);

        var acceptedEvent = BuildEvent(
            normalizedSnapshot.OperationId,
            sequence: 1,
            status: normalizedSnapshot.Status,
            eventCode: "accepted",
            message: "Operation accepted for observation.",
            occurredAt: normalizedSnapshot.CreatedAt.Clone());

        lock (_gate)
        {
            if (_records.ContainsKey(normalizedSnapshot.OperationId))
                throw new InvalidOperationException($"Operation '{normalizedSnapshot.OperationId}' already exists.");

            _records[normalizedSnapshot.OperationId] = new AppOperationRecord(
                normalizedSnapshot.Clone(),
                [acceptedEvent],
                result: null);
        }

        return Task.FromResult(normalizedSnapshot);
    }

    public Task<AppOperationSnapshot> AdvanceAsync(
        AppOperationUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ct.ThrowIfCancellationRequested();

        var normalizedOperationId = NormalizeExistingOperationId(update.OperationId);
        if (normalizedOperationId.Length == 0)
            throw new InvalidOperationException("operationId is required.");
        if (update.Status == AppOperationStatus.Unspecified)
            throw new InvalidOperationException("status is required.");

        AppOperationEvent operationEvent;
        AppOperationSnapshot nextSnapshot;
        bool terminal;

        lock (_gate)
        {
            if (!_records.TryGetValue(normalizedOperationId, out var record))
                throw new InvalidOperationException($"Operation '{normalizedOperationId}' was not found.");
            if (IsTerminal(record.Snapshot.Status))
                throw new InvalidOperationException($"Operation '{normalizedOperationId}' is already terminal.");

            var occurredAt = update.OccurredAt ?? UtcNowTimestamp();
            var sequence = checked((ulong)record.Events.Count + 1);
            operationEvent = BuildEvent(
                normalizedOperationId,
                sequence,
                update.Status,
                NormalizeEventCode(update.EventCode, update.Status),
                NormalizeMessage(update.Message, update.Status),
                occurredAt);

            record.Snapshot.Status = update.Status;
            nextSnapshot = record.Snapshot.Clone();
            record.Events.Add(operationEvent.Clone());

            if (update.Result != null)
            {
                if (!IsTerminal(update.Status))
                    throw new InvalidOperationException("result can only be recorded for terminal operation states.");

                record.Result = NormalizeResult(update.Result, normalizedOperationId, update.Status, occurredAt);
            }
            else if (IsTerminal(update.Status))
            {
                record.Result = BuildSyntheticResult(normalizedOperationId, update.Status, operationEvent);
            }

            terminal = IsTerminal(update.Status);
        }

        PublishEvent(normalizedOperationId, operationEvent, completeWatchers: terminal);
        return Task.FromResult(nextSnapshot);
    }

    public Task<AppOperationSnapshot?> GetAsync(
        string operationId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedOperationId = NormalizeExistingOperationId(operationId);
        if (normalizedOperationId.Length == 0)
            return Task.FromResult<AppOperationSnapshot?>(null);

        lock (_gate)
        {
            return Task.FromResult(
                _records.TryGetValue(normalizedOperationId, out var record)
                    ? record.Snapshot.Clone()
                    : null);
        }
    }

    public Task<AppOperationResult?> GetResultAsync(
        string operationId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedOperationId = NormalizeExistingOperationId(operationId);
        if (normalizedOperationId.Length == 0)
            return Task.FromResult<AppOperationResult?>(null);

        lock (_gate)
        {
            return Task.FromResult(
                _records.TryGetValue(normalizedOperationId, out var record) && record.Result != null
                    ? record.Result.Clone()
                    : null);
        }
    }

    public Task<IReadOnlyList<AppOperationEvent>> ListEventsAsync(
        string operationId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedOperationId = NormalizeExistingOperationId(operationId);
        if (normalizedOperationId.Length == 0)
            return Task.FromResult<IReadOnlyList<AppOperationEvent>>([]);

        lock (_gate)
        {
            if (!_records.TryGetValue(normalizedOperationId, out var record))
                return Task.FromResult<IReadOnlyList<AppOperationEvent>>([]);

            return Task.FromResult<IReadOnlyList<AppOperationEvent>>(
                record.Events
                    .OrderBy(static x => x.Sequence)
                    .Select(static x => x.Clone())
                    .ToArray());
        }
    }

    public async IAsyncEnumerable<AppOperationEvent> WatchAsync(
        string operationId,
        ulong afterSequence = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedOperationId = NormalizeExistingOperationId(operationId);
        if (normalizedOperationId.Length == 0)
            yield break;

        Channel<AppOperationEvent>? channel = null;
        AppOperationEvent[] historical;
        lock (_gate)
        {
            if (!_records.TryGetValue(normalizedOperationId, out var record))
                yield break;

            historical = record.Events
                .Where(x => x.Sequence > afterSequence)
                .OrderBy(x => x.Sequence)
                .Select(x => x.Clone())
                .ToArray();

            if (!IsTerminal(record.Snapshot.Status))
            {
                channel = Channel.CreateUnbounded<AppOperationEvent>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
                record.Watchers.Add(channel);
            }
        }

        try
        {
            foreach (var operationEvent in historical)
                yield return operationEvent;

            if (channel == null)
                yield break;

            while (await channel.Reader.WaitToReadAsync(ct))
            {
                while (channel.Reader.TryRead(out var operationEvent))
                {
                    yield return operationEvent.Clone();
                    if (IsTerminal(operationEvent.Status))
                        yield break;
                }
            }
        }
        finally
        {
            if (channel != null)
            {
                lock (_gate)
                {
                    if (_records.TryGetValue(normalizedOperationId, out var record))
                        record.Watchers.Remove(channel);
                }
            }
        }
    }

    private void PublishEvent(string operationId, AppOperationEvent operationEvent, bool completeWatchers)
    {
        List<Channel<AppOperationEvent>> watchers;
        lock (_gate)
        {
            if (!_records.TryGetValue(operationId, out var record))
                return;

            watchers = record.Watchers.ToList();
            if (completeWatchers)
                record.Watchers.Clear();
        }

        foreach (var watcher in watchers)
        {
            watcher.Writer.TryWrite(operationEvent.Clone());
            if (completeWatchers)
                watcher.Writer.TryComplete();
        }
    }

    private static AppOperationEvent BuildEvent(
        string operationId,
        ulong sequence,
        AppOperationStatus status,
        string eventCode,
        string message,
        Timestamp occurredAt) =>
        new()
        {
            OperationId = operationId,
            Sequence = sequence,
            Status = status,
            EventCode = eventCode,
            Message = message,
            OccurredAt = occurredAt.Clone(),
        };

    private static AppOperationResult NormalizeResult(
        AppOperationResult result,
        string operationId,
        AppOperationStatus status,
        Timestamp completedAt)
    {
        var normalized = result.Clone();
        normalized.OperationId = operationId;
        normalized.Status = status;
        normalized.CompletedAt ??= completedAt.Clone();
        normalized.ResultCode = NormalizeOptional(normalized.ResultCode) ?? DefaultResultCode(status);
        normalized.Message = NormalizeOptional(normalized.Message) ?? DefaultResultMessage(status);
        return normalized;
    }

    private static AppOperationResult BuildSyntheticResult(
        string operationId,
        AppOperationStatus status,
        AppOperationEvent operationEvent) =>
        new()
        {
            OperationId = operationId,
            Status = status,
            ResultCode = NormalizeOptional(operationEvent.EventCode) ?? DefaultResultCode(status),
            Message = NormalizeOptional(operationEvent.Message) ?? DefaultResultMessage(status),
            CompletedAt = operationEvent.OccurredAt?.Clone() ?? UtcNowTimestamp(),
        };

    private static string NormalizeOperationId(string? operationId)
    {
        var normalized = NormalizeExistingOperationId(operationId);
        return normalized.Length == 0
            ? $"appop_{Guid.NewGuid():N}"
            : normalized;
    }

    private static string NormalizeExistingOperationId(string? operationId) =>
        NormalizeOptional(operationId) ?? string.Empty;

    private static string NormalizeEventCode(string? eventCode, AppOperationStatus status) =>
        NormalizeOptional(eventCode) ?? DefaultEventCode(status);

    private static string NormalizeMessage(string? message, AppOperationStatus status) =>
        NormalizeOptional(message) ?? DefaultResultMessage(status);

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsTerminal(AppOperationStatus status) =>
        status is AppOperationStatus.Completed or AppOperationStatus.Failed or AppOperationStatus.Cancelled;

    private static string DefaultEventCode(AppOperationStatus status) =>
        status switch
        {
            AppOperationStatus.Accepted => "accepted",
            AppOperationStatus.Running => "running",
            AppOperationStatus.Completed => "completed",
            AppOperationStatus.Failed => "failed",
            AppOperationStatus.Cancelled => "cancelled",
            _ => "updated",
        };

    private static string DefaultResultCode(AppOperationStatus status) =>
        status switch
        {
            AppOperationStatus.Completed => "completed",
            AppOperationStatus.Failed => "failed",
            AppOperationStatus.Cancelled => "cancelled",
            _ => "updated",
        };

    private static string DefaultResultMessage(AppOperationStatus status) =>
        status switch
        {
            AppOperationStatus.Accepted => "Operation accepted for observation.",
            AppOperationStatus.Running => "Operation is running.",
            AppOperationStatus.Completed => "Operation completed.",
            AppOperationStatus.Failed => "Operation failed.",
            AppOperationStatus.Cancelled => "Operation cancelled.",
            _ => "Operation updated.",
        };

    private static Timestamp UtcNowTimestamp() =>
        Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc));

    private sealed class AppOperationRecord
    {
        public AppOperationRecord(
            AppOperationSnapshot snapshot,
            IEnumerable<AppOperationEvent> events,
            AppOperationResult? result)
        {
            Snapshot = snapshot;
            Events = events.ToList();
            Result = result;
        }

        public AppOperationSnapshot Snapshot { get; }

        public List<AppOperationEvent> Events { get; }

        public AppOperationResult? Result { get; set; }

        public List<Channel<AppOperationEvent>> Watchers { get; } = [];
    }
}
