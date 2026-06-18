using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

public sealed partial class SkillRunnerState
{
    internal const int RecentDeliveriesCap = 100;

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

    public bool IsCronOccurrenceTerminal(string? cronOccurrenceKey)
    {
        var normalized = NormalizeCronOccurrenceKey(cronOccurrenceKey);
        return !string.IsNullOrEmpty(normalized) &&
               RecentCronOccurrenceTerminals.Any(record =>
                   string.Equals(
                       NormalizeCronOccurrenceKey(record.CronOccurrenceKey),
                       normalized,
                       StringComparison.Ordinal));
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

    internal void AppendDelivery(DeliveryProducedEvent produced)
    {
        ArgumentNullException.ThrowIfNull(produced);

        var entry = ToLedgerEntry(produced);
        RecentDeliveries.Add(entry);
        while (RecentDeliveries.Count > RecentDeliveriesCap)
            RecentDeliveries.RemoveAt(0);

        if (entry.Status == DeliveryStatus.Succeeded)
            LastSuccessfulDelivery = entry.Clone();
    }

    internal void UpsertCronOccurrenceTerminal(string? cronOccurrenceKey, Timestamp terminalAt)
    {
        ArgumentNullException.ThrowIfNull(terminalAt);

        var normalized = NormalizeCronOccurrenceKey(cronOccurrenceKey);
        if (string.IsNullOrEmpty(normalized))
            return;

        var existingIndex = FindCronOccurrenceTerminalIndex(normalized);
        var next = new SkillRunnerCronOccurrenceTerminalRecord
        {
            CronOccurrenceKey = normalized,
            TerminalAt = terminalAt,
        };

        if (existingIndex >= 0)
            RecentCronOccurrenceTerminals[existingIndex] = next;
        else
            RecentCronOccurrenceTerminals.Add(next);
    }

    internal void TrimCronOccurrenceTerminals(DateTimeOffset now)
    {
        var cutoff = now - SkillRunnerDefaults.CronOccurrenceTerminalRetentionAge;
        var keysToRemove = RecentCronOccurrenceTerminals
            .OrderByDescending(static record => ToDateTimeOffset(record.TerminalAt))
            .Skip(SkillRunnerDefaults.CronOccurrenceTerminalRetention)
            .Concat(RecentCronOccurrenceTerminals
                .Where(record => ToDateTimeOffset(record.TerminalAt) < cutoff))
            .Select(static record => NormalizeCronOccurrenceKey(record.CronOccurrenceKey))
            .Where(static key => !string.IsNullOrEmpty(key))
            .ToHashSet(StringComparer.Ordinal);

        if (keysToRemove.Count == 0)
            return;

        var kept = RecentCronOccurrenceTerminals
            .Where(record => !keysToRemove.Contains(NormalizeCronOccurrenceKey(record.CronOccurrenceKey)))
            .Select(static record => record.Clone())
            .ToArray();
        RecentCronOccurrenceTerminals.Clear();
        RecentCronOccurrenceTerminals.AddRange(kept);
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

    private int FindCronOccurrenceTerminalIndex(string normalizedCronOccurrenceKey)
    {
        for (var i = 0; i < RecentCronOccurrenceTerminals.Count; i++)
        {
            var record = RecentCronOccurrenceTerminals[i];
            if (string.Equals(
                    NormalizeCronOccurrenceKey(record.CronOccurrenceKey),
                    normalizedCronOccurrenceKey,
                    StringComparison.Ordinal))
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

    private static string NormalizeCronOccurrenceKey(string? cronOccurrenceKey) =>
        cronOccurrenceKey?.Trim() ?? string.Empty;

    private static DeliveryLedgerEntry ToLedgerEntry(DeliveryProducedEvent produced) =>
        new()
        {
            DeliveryKind = produced.DeliveryKind,
            Status = produced.Status,
            Target = produced.Target?.Clone() ?? new DeliveryTarget(),
            LarkMessageId = produced.LarkMessageId ?? string.Empty,
            CardId = produced.CardId ?? string.Empty,
            RequestId = produced.RequestId ?? string.Empty,
            SourceEventId = produced.SourceEventId ?? string.Empty,
            ProducedAtVersion = produced.ProducedAtVersion,
        };
}
