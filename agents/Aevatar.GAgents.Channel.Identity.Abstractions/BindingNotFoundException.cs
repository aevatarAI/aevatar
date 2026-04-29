namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Thrown when a broker operation references an external subject that has
/// never been bound (or was bound and the local readmodel has not yet caught
/// up after a revoke). Distinct from <see cref="BindingRevokedException"/>,
/// which is reserved for NyxID-side revocation signals on a binding that
/// previously existed (HTTP 400 <c>invalid_grant</c>).
/// </summary>
/// <remarks>
/// Caller behaviour:
/// <list type="bullet">
///   <item>Outbound / turn path: prompt the sender to run <c>/init</c>.</item>
///   <item>Do NOT fall back to bot-owner credentials or any cached token
///   (ADR-0017 §Implementation Notes #4).</item>
/// </list>
/// </remarks>
public sealed class BindingNotFoundException : Exception
{
    /// <summary>
    /// External subject for which no active binding could be located.
    /// </summary>
    public ExternalSubjectRef ExternalSubject { get; }

    /// <summary>
    /// Creates a new <see cref="BindingNotFoundException"/>.
    /// </summary>
    public BindingNotFoundException(ExternalSubjectRef externalSubject, string? message = null, Exception? innerException = null)
        : base(message ?? $"No active binding for {externalSubject.Platform}:{externalSubject.Tenant}:{externalSubject.ExternalUserId}", innerException)
    {
        ExternalSubject = externalSubject;
    }
}
