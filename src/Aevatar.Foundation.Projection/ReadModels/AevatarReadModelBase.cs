namespace Aevatar.Foundation.Projection.ReadModels;

/// <summary>
/// Minimal cross-domain read model metadata.
/// Keep this base workflow-agnostic: domain-specific identifiers must stay in domain read models.
/// </summary>
public abstract class AevatarReadModelBase
    : ProjectionReadModelBase<string>
{
}
