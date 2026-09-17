namespace Aevatar.GAgentService.Governance.Abstractions;

public enum ActivationCapabilityViewProjection
{
    Unspecified = 0,
    ServiceCatalog = 1,
}

public sealed class ActivationCapabilityViewNotReadyException : InvalidOperationException
{
    public ActivationCapabilityViewNotReadyException(
        string serviceKey,
        string revisionId,
        ActivationCapabilityViewProjection projection)
        : base(BuildMessage(serviceKey, revisionId, projection))
    {
        ServiceKey = string.IsNullOrWhiteSpace(serviceKey)
            ? throw new ArgumentException("Service key is required.", nameof(serviceKey))
            : serviceKey;
        RevisionId = string.IsNullOrWhiteSpace(revisionId)
            ? throw new ArgumentException("Revision id is required.", nameof(revisionId))
            : revisionId;
        Projection = projection != ActivationCapabilityViewProjection.Unspecified
            ? projection
            : throw new ArgumentOutOfRangeException(nameof(projection));
    }

    public string ServiceKey { get; }

    public string RevisionId { get; }

    public ActivationCapabilityViewProjection Projection { get; }

    private static string BuildMessage(
        string serviceKey,
        string revisionId,
        ActivationCapabilityViewProjection projection) =>
        $"Activation capability view for service '{serviceKey}' revision '{revisionId}' is not ready because the '{projection}' projection is unavailable.";
}
