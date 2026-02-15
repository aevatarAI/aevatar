using System.Collections.Concurrent;

namespace Aevatar.Demos.CaseProjection.Abstractions;

/// <summary>
/// Per-run context for case projection.
/// </summary>
public sealed class CaseProjectionContext : IProjectionRunContext
{
    public required string RunId { get; init; }
    public required string RootActorId { get; init; }
    public required string CaseId { get; init; }
    public required string CaseType { get; init; }
    public required string Input { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    private readonly ConcurrentDictionary<string, byte> _processedEventIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object?> _properties = new(StringComparer.Ordinal);

    public bool TryMarkProcessed(string eventId) => _processedEventIds.TryAdd(eventId, 0);

    public void SetProperty(string key, object? value) => _properties[key] = value;

    public bool TryGetProperty<T>(string key, out T? value)
    {
        if (_properties.TryGetValue(key, out var raw) && raw is T casted)
        {
            value = casted;
            return true;
        }

        value = default;
        return false;
    }
}
