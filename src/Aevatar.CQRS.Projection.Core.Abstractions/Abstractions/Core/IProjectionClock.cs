namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Provides current UTC time for projection runtime.
/// </summary>
public interface IProjectionClock
{
    DateTimeOffset UtcNow { get; }
}
