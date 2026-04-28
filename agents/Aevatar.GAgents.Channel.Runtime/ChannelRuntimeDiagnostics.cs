using System.Collections.Immutable;

namespace Aevatar.GAgents.Channel.Runtime;

public interface IChannelRuntimeDiagnostics
{
    void Record(string stage, string platform, string registrationId, string? detail = null);

    IReadOnlyList<ChannelRuntimeDiagnosticEntry> GetRecent();
}

public sealed record ChannelRuntimeDiagnosticEntry(
    DateTimeOffset Timestamp,
    string Stage,
    string Platform,
    string RegistrationId,
    string? Detail = null);

public sealed class InMemoryChannelRuntimeDiagnostics : IChannelRuntimeDiagnostics
{
    private const int MaxEntries = 50;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private ImmutableList<ChannelRuntimeDiagnosticEntry> _entries = ImmutableList<ChannelRuntimeDiagnosticEntry>.Empty;

    public void Record(string stage, string platform, string registrationId, string? detail = null)
    {
        var entry = new ChannelRuntimeDiagnosticEntry(
            DateTimeOffset.UtcNow,
            stage,
            platform,
            registrationId,
            detail);

        ImmutableInterlocked.Update(ref _entries, current => TrimAndAppend(current, entry));
    }

    public IReadOnlyList<ChannelRuntimeDiagnosticEntry> GetRecent()
    {
        ImmutableInterlocked.Update(ref _entries, current => Trim(current, DateTimeOffset.UtcNow));
        return _entries;
    }

    private static ImmutableList<ChannelRuntimeDiagnosticEntry> TrimAndAppend(
        ImmutableList<ChannelRuntimeDiagnosticEntry> current,
        ChannelRuntimeDiagnosticEntry entry)
    {
        var next = Trim(current, entry.Timestamp).Add(entry);
        if (next.Count <= MaxEntries)
            return next;

        return next.RemoveRange(0, next.Count - MaxEntries);
    }

    private static ImmutableList<ChannelRuntimeDiagnosticEntry> Trim(
        ImmutableList<ChannelRuntimeDiagnosticEntry> current,
        DateTimeOffset now)
    {
        var cutoff = now - Retention;
        var firstRetainedIndex = 0;
        while (firstRetainedIndex < current.Count && current[firstRetainedIndex].Timestamp < cutoff)
            firstRetainedIndex++;

        if (firstRetainedIndex > 0)
            current = current.RemoveRange(0, firstRetainedIndex);

        if (current.Count <= MaxEntries)
            return current;

        return current.RemoveRange(0, current.Count - MaxEntries);
    }
}
