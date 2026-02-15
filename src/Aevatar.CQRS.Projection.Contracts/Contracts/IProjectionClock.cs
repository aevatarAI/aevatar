namespace Aevatar.CQRS.Projection.Contracts;

/// <summary>
/// Provides current UTC time for projection runtime.
/// </summary>
public interface IProjectionClock
{
    DateTimeOffset UtcNow { get; }
}
