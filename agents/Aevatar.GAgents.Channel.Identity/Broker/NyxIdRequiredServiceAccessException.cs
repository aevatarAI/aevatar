namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Indicates that an OAuth authorization-code flow did not grant all NyxID
/// service resources required by aevatar.
/// </summary>
public sealed class NyxIdRequiredServiceAccessException : Exception
{
    public IReadOnlyList<string> RequiredResources { get; }

    public NyxIdRequiredServiceAccessException(
        IEnumerable<string> requiredResources,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? "NyxID authorization did not grant every required service.", innerException)
    {
        ArgumentNullException.ThrowIfNull(requiredResources);
        RequiredResources = requiredResources
            .Where(static resource => !string.IsNullOrWhiteSpace(resource))
            .Select(static resource => resource.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (RequiredResources.Count == 0)
            throw new ArgumentException("At least one required resource must be provided.", nameof(requiredResources));
    }
}
