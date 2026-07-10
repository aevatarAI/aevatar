namespace Aevatar.Foundation.VoicePresence;

/// <summary>
/// VoicePresence module runtime options.
/// </summary>
public sealed class VoicePresenceModuleOptions
{
    private IReadOnlyList<string> _directExternalEventTypeUrls = [];

    public string Name { get; init; } = "voice_presence";

    public int Priority { get; init; }

    public string? LinkId { get; init; }

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ToolExecutionTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int PendingInjectionCapacity { get; init; } = 16;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public IReadOnlyList<string> DirectExternalEventTypeUrls
    {
        get => _directExternalEventTypeUrls;
        init => _directExternalEventTypeUrls = NormalizeTypeUrls(value);
    }

    public VoiceDirectExternalEventNoActiveSessionPolicy DirectExternalEventNoActiveSessionPolicy { get; init; } =
        VoiceDirectExternalEventNoActiveSessionPolicy.DropAndLog;

    private static IReadOnlyList<string> NormalizeTypeUrls(IEnumerable<string>? typeUrls)
    {
        if (typeUrls == null)
            return [];

        return typeUrls
            .Select(static typeUrl => typeUrl.Trim())
            .Where(static typeUrl => !string.IsNullOrWhiteSpace(typeUrl))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

public enum VoiceDirectExternalEventNoActiveSessionPolicy
{
    DropAndLog = 0,
}
