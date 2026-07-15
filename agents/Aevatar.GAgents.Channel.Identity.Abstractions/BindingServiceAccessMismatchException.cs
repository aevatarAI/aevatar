using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Thrown when a NyxID binding can mint a token but the grant does not include
/// every service resource required by aevatar.
/// </summary>
public sealed class BindingServiceAccessMismatchException : Exception
{
    /// <summary>
    /// External subject whose binding lacks the required service access.
    /// </summary>
    public ExternalSubjectRef ExternalSubject { get; }

    /// <summary>
    /// RFC 8707 resource URIs that the binding must grant.
    /// </summary>
    public IReadOnlyList<string> RequiredResources { get; }

    /// <summary>
    /// Creates a service-access mismatch for an existing binding.
    /// </summary>
    public BindingServiceAccessMismatchException(
        ExternalSubjectRef externalSubject,
        IEnumerable<string> requiredResources,
        string? message = null,
        Exception? innerException = null)
        : base(
            message ?? $"Binding service access mismatch for {externalSubject.Platform}:{externalSubject.Tenant}:{externalSubject.ExternalUserId}",
            innerException)
    {
        ArgumentNullException.ThrowIfNull(requiredResources);
        ExternalSubject = externalSubject;
        RequiredResources = requiredResources
            .Where(static resource => !string.IsNullOrWhiteSpace(resource))
            .Select(static resource => resource.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (RequiredResources.Count == 0)
            throw new ArgumentException("At least one required resource must be provided.", nameof(requiredResources));
    }
}
