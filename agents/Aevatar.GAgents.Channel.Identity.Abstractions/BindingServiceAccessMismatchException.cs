using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Thrown when a NyxID binding can mint a token but the grant does not include
/// the service resource required by aevatar.
/// </summary>
public sealed class BindingServiceAccessMismatchException : Exception
{
    /// <summary>
    /// External subject whose binding lacks the required service access.
    /// </summary>
    public ExternalSubjectRef ExternalSubject { get; }

    /// <summary>
    /// RFC 8707 resource URI that the binding must grant.
    /// </summary>
    public string RequiredResource { get; }

    /// <summary>
    /// Creates a service-access mismatch for an existing binding.
    /// </summary>
    public BindingServiceAccessMismatchException(
        ExternalSubjectRef externalSubject,
        string requiredResource,
        string? message = null,
        Exception? innerException = null)
        : base(
            message ?? $"Binding service access mismatch for {externalSubject.Platform}:{externalSubject.Tenant}:{externalSubject.ExternalUserId}",
            innerException)
    {
        ExternalSubject = externalSubject;
        RequiredResource = requiredResource;
    }
}
