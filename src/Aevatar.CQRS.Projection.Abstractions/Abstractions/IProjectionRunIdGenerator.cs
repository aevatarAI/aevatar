namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Provides run identifiers for projection sessions.
/// </summary>
public interface IProjectionRunIdGenerator
{
    string NextRunId();
}
