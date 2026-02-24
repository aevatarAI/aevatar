namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionProviderCapabilityValidationException : InvalidOperationException
{
    public ProjectionProviderCapabilityValidationException(
        Type readModelType,
        ProjectionStoreRequirements requirements,
        ProjectionProviderCapabilities capabilities,
        IReadOnlyList<string> violations)
        : base(BuildMessage(readModelType, capabilities.ProviderName, violations))
    {
        ReadModelType = readModelType;
        Requirements = requirements;
        Capabilities = capabilities;
        Violations = violations;
    }

    public Type ReadModelType { get; }

    public ProjectionStoreRequirements Requirements { get; }

    public ProjectionProviderCapabilities Capabilities { get; }

    public IReadOnlyList<string> Violations { get; }

    private static string BuildMessage(
        Type readModelType,
        string providerName,
        IReadOnlyList<string> violations) =>
        $"ReadModel '{readModelType.FullName}' is not supported by provider '{providerName}'. " +
        $"Violations: {string.Join("; ", violations)}";
}
