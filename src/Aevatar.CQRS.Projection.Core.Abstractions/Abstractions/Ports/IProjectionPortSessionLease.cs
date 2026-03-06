namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic session lease identity contract used by projection sink subscription orchestration.
/// </summary>
public interface IProjectionPortSessionLease
{
    string ScopeId { get; }

    string SessionId { get; }
}
