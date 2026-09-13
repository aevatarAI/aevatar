using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// The binding retained by a request no longer matches the sender's current
/// binding. This invalidates the request; it must not revoke the newer binding.
/// </summary>
public sealed class BindingChangedException(ExternalSubjectRef externalSubject)
    : Exception("The sender binding changed; the request must be planned again.")
{
    /// <summary>The external subject whose retained binding became stale.</summary>
    public ExternalSubjectRef ExternalSubject { get; } = externalSubject.Clone();
}
