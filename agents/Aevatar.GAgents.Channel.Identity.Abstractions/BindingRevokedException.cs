namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Thrown by <see cref="INyxIdCapabilityBroker.IssueShortLivedAsync"/> when
/// NyxID reports the binding as revoked (HTTP 400 <c>invalid_grant</c>).
/// Callers MUST event-source revoke the local binding actor and prompt the
/// sender to run <c>/init</c> again. See ADR-0017 Decision §invalid_grant.
/// </summary>
public sealed class BindingRevokedException : Exception
{
    /// <summary>
    /// External subject whose binding NyxID reports as revoked.
    /// </summary>
    public ExternalSubjectRef ExternalSubject { get; }

    /// <summary>
    /// Creates a new <see cref="BindingRevokedException"/>.
    /// </summary>
    public BindingRevokedException(ExternalSubjectRef externalSubject, string? message = null, Exception? innerException = null)
        : base(message ?? $"Binding revoked for {externalSubject.Platform}:{externalSubject.Tenant}:{externalSubject.ExternalUserId}", innerException)
    {
        ExternalSubject = externalSubject;
    }
}
