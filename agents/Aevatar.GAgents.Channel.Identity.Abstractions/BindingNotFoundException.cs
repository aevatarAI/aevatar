using Aevatar.GAgents.Channel.Abstractions;

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
///   <item>Binding-required commands: prompt the sender to run <c>/init</c>.</item>
///   <item>Normal LLM turns: treat the sender config as unavailable and fall
///   back to the bot owner's LLM credentials.</item>
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
