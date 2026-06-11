using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Thrown by <see cref="INyxIdCapabilityBroker.IssueShortLivedAsync"/> when
/// NyxID reports that the existing binding cannot mint the requested scope
/// (HTTP 400 <c>invalid_scope</c>). Binding-required callers should ask the
/// user to re-run <c>/init</c>; normal LLM turns may fall back to the bot
/// owner's LLM credentials.
/// </summary>
public sealed class BindingScopeMismatchException : Exception
{
    /// <summary>
    /// External subject whose binding is missing the requested scope.
    /// </summary>
    public ExternalSubjectRef ExternalSubject { get; }

    /// <summary>
    /// Creates a new <see cref="BindingScopeMismatchException"/>.
    /// </summary>
    public BindingScopeMismatchException(ExternalSubjectRef externalSubject, string? message = null, Exception? innerException = null)
        : base(message ?? $"Binding scope mismatch for {externalSubject.Platform}:{externalSubject.Tenant}:{externalSubject.ExternalUserId}", innerException)
    {
        ExternalSubject = externalSubject;
    }
}
