using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

public sealed partial class SkillRunnerState : ISchedulable
{
    /// <inheritdoc />
    ScheduleState ISchedulable.Schedule => new()
    {
        Enabled = Enabled,
        Cron = ScheduleCron ?? string.Empty,
        Timezone = ScheduleTimezone ?? string.Empty,
        NextRunAt = NextRunAt,
        LastRunAt = LastRunAt,
        ErrorCount = ErrorCount,
        Mode = ScheduleMode == SkillRunnerScheduleMode.OneShot
            ? ScheduleState.ModeOneShot
            : ScheduleState.ModeCron,
        RunAt = OneShotRunAt,
        RetiredAt = RetiredAt,
    };

    public ExternalTriggerSource? FindExternalTriggerSource(string? sourceId)
    {
        var normalized = sourceId?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return null;

        return ExternalTriggerSources.FirstOrDefault(source =>
            string.Equals(source.SourceId?.Trim(), normalized, StringComparison.Ordinal));
    }

    public SkillRunnerExternalTriggerDeliveryRecord? FindExternalTriggerDelivery(
        SkillRunnerExternalTriggerIdentity? identity)
    {
        var sourceId = identity?.SourceId?.Trim();
        var deliveryId = identity?.DeliveryId?.Trim();
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(deliveryId))
            return null;

        return RecentExternalTriggerDeliveries.FirstOrDefault(record =>
            string.Equals(record.Identity?.SourceId?.Trim(), sourceId, StringComparison.Ordinal) &&
            string.Equals(record.Identity?.DeliveryId?.Trim(), deliveryId, StringComparison.Ordinal));
    }

    public bool IsExternalTriggerTerminal(SkillRunnerExternalTriggerIdentity? identity)
    {
        var record = FindExternalTriggerDelivery(identity);
        return record is not null && IsTerminalExternalTriggerStatus(record.Status);
    }

    public IReadOnlyList<SkillRunnerExternalTriggerDeliveryRecord> RecoverableExternalTriggerDeliveries() =>
        RecentExternalTriggerDeliveries
            .Where(static record => record.Identity is not null)
            .Where(static record => record.Status is
                SkillRunnerExternalTriggerDeliveryStatus.Admitted or
                SkillRunnerExternalTriggerDeliveryStatus.DispatchRequested)
            .ToArray();

    internal void UpsertExternalTriggerDelivery(
        SkillRunnerExternalTriggerIdentity identity,
        SkillRunnerExternalTriggerDeliveryStatus status,
        Timestamp updatedAt,
        string reason = "",
        int? dispatchAttempt = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(updatedAt);

        var existingIndex = FindExternalTriggerDeliveryIndex(identity);
        var existing = existingIndex >= 0
            ? RecentExternalTriggerDeliveries[existingIndex]
            : null;
        var next = existing?.Clone() ?? new SkillRunnerExternalTriggerDeliveryRecord
        {
            Identity = identity.Clone(),
            AdmittedAt = updatedAt,
        };
        next.Identity = identity.Clone();
        next.Status = status;
        next.UpdatedAt = updatedAt;
        next.Reason = reason ?? string.Empty;
        if (dispatchAttempt.HasValue)
            next.DispatchAttempt = dispatchAttempt.Value;

        if (existingIndex >= 0)
            RecentExternalTriggerDeliveries[existingIndex] = next;
        else
            RecentExternalTriggerDeliveries.Add(next);
    }

    internal void TrimExternalTriggerDeliveries(DateTimeOffset now)
    {
        var cutoff = now - SkillRunnerDefaults.ExternalTriggerTerminalDeliveryRetentionAge;
        var terminal = RecentExternalTriggerDeliveries
            .Where(static record => IsTerminalExternalTriggerRecord(record))
            .OrderByDescending(static record => ToDateTimeOffset(record.UpdatedAt))
            .ToArray();
        var terminalToRemove = terminal
            .Skip(SkillRunnerDefaults.ExternalTriggerTerminalDeliveryRetention)
            .Concat(terminal.Where(record => ToDateTimeOffset(record.UpdatedAt) < cutoff))
            .DistinctBy(static record => BuildDeliveryKey(record.Identity))
            .Select(static record => BuildDeliveryKey(record.Identity))
            .ToHashSet(StringComparer.Ordinal);

        if (terminalToRemove.Count == 0)
            return;

        var kept = RecentExternalTriggerDeliveries
            .Where(record => !terminalToRemove.Contains(BuildDeliveryKey(record.Identity)))
            .Select(static record => record.Clone())
            .ToArray();
        RecentExternalTriggerDeliveries.Clear();
        RecentExternalTriggerDeliveries.AddRange(kept);
    }

    private int FindExternalTriggerDeliveryIndex(SkillRunnerExternalTriggerIdentity identity)
    {
        for (var i = 0; i < RecentExternalTriggerDeliveries.Count; i++)
        {
            var record = RecentExternalTriggerDeliveries[i];
            if (string.Equals(record.Identity?.SourceId?.Trim(), identity.SourceId?.Trim(), StringComparison.Ordinal) &&
                string.Equals(record.Identity?.DeliveryId?.Trim(), identity.DeliveryId?.Trim(), StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsTerminalExternalTriggerRecord(SkillRunnerExternalTriggerDeliveryRecord record) =>
        IsTerminalExternalTriggerStatus(record.Status);

    private static bool IsTerminalExternalTriggerStatus(SkillRunnerExternalTriggerDeliveryStatus status) =>
        status is
            SkillRunnerExternalTriggerDeliveryStatus.Completed or
            SkillRunnerExternalTriggerDeliveryStatus.Failed or
            SkillRunnerExternalTriggerDeliveryStatus.Rejected or
            SkillRunnerExternalTriggerDeliveryStatus.DuplicateIgnored;

    private static DateTimeOffset ToDateTimeOffset(Timestamp? timestamp) =>
        timestamp?.ToDateTimeOffset() ?? DateTimeOffset.MinValue;

    private static string BuildDeliveryKey(SkillRunnerExternalTriggerIdentity? identity) =>
        $"{identity?.SourceId?.Trim() ?? string.Empty}\n{identity?.DeliveryId?.Trim() ?? string.Empty}";
}
