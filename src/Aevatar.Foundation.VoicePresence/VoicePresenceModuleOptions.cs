namespace Aevatar.Foundation.VoicePresence;

/// <summary>
/// VoicePresence module runtime options.
/// </summary>
public sealed class VoicePresenceModuleOptions
{
    public string Name { get; init; } = "voice_presence";

    public int Priority { get; init; }

    public string? LinkId { get; init; }

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ToolExecutionTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int PendingInjectionCapacity { get; init; } = 16;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
