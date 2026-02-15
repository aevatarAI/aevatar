namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Provides current UTC time for projection runtime.
/// </summary>
public interface IProjectionClock
{
    DateTimeOffset UtcNow { get; }
}
