namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Default projection clock based on system UTC time.
/// </summary>
public sealed class SystemProjectionClock : IProjectionClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
