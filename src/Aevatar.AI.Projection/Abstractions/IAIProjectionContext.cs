namespace Aevatar.AI.Projection.Abstractions;

/// <summary>
/// Minimal projection context contract required by AI default appliers.
/// </summary>
public interface IAIProjectionContext
{
    string RootActorId { get; }
}
