namespace Aevatar.CQRS.Projection.Contracts;

/// <summary>
/// Provides run identifiers for projection sessions.
/// </summary>
public interface IProjectionRunIdGenerator
{
    string NextRunId();
}
