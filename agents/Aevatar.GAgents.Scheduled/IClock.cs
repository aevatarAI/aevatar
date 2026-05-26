namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Provides the current UTC wall clock for scheduled-agent decisions.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Default wall clock backed by the system UTC clock.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
