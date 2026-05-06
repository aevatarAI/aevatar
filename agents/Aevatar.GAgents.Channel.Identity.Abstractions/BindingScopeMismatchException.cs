using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Thrown by <see cref="INyxIdCapabilityBroker.IssueShortLivedAsync"/> when
/// NyxID reports that the existing binding cannot mint the requested scope
/// (HTTP 400 <c>invalid_scope</c>). The user must re-run <c>/init</c> so the
/// binding is recreated against the current OAuth client scopes.
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
