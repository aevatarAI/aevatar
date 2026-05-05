using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Thrown by <see cref="INyxIdCapabilityBroker.IssueShortLivedAsync"/> when
/// NyxID reports the binding as revoked (HTTP 400 <c>invalid_grant</c>).
/// Binding-required callers should prompt the sender to run <c>/init</c>
/// again; normal LLM turns may fall back to the bot owner's LLM credentials.
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
