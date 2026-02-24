namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class ProjectionReadModelCapabilityValidationException : InvalidOperationException
{
    public ProjectionReadModelCapabilityValidationException(
        Type readModelType,
        ProjectionReadModelRequirements requirements,
        ProjectionReadModelProviderCapabilities capabilities,
        IReadOnlyList<string> violations)
        : base(BuildMessage(readModelType, capabilities.ProviderName, violations))
    {
        ReadModelType = readModelType;
        Requirements = requirements;
        Capabilities = capabilities;
        Violations = violations;
    }

    public Type ReadModelType { get; }

    public ProjectionReadModelRequirements Requirements { get; }

    public ProjectionReadModelProviderCapabilities Capabilities { get; }

    public IReadOnlyList<string> Violations { get; }

    private static string BuildMessage(
        Type readModelType,
        string providerName,
        IReadOnlyList<string> violations) =>
        $"ReadModel '{readModelType.FullName}' is not supported by provider '{providerName}'. " +
        $"Violations: {string.Join("; ", violations)}";
}
