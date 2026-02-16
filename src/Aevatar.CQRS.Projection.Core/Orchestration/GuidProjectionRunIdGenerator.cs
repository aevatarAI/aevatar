namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Default run-id generator for projection runs.
/// </summary>
public sealed class GuidProjectionRunIdGenerator : IProjectionRunIdGenerator
{
    public string NextRunId() => Guid.NewGuid().ToString("N");
}
