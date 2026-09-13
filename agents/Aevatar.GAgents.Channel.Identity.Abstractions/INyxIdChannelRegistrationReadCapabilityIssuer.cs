using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Issues a request-local capability for a verified sender's own Channel registration read.
/// Callers must restrict its use to discovering and executing that exact read; it does not
/// authorize the platform registration or require unrelated Aevatar runtime services.
/// </summary>
public interface INyxIdChannelRegistrationReadCapabilityIssuer
{
    /// <summary>Exchanges the verified external binding for a short-lived sender read capability.</summary>
    Task<CapabilityHandle> IssueByBindingIdAsync(
        ExternalSubjectRef externalSubject,
        string bindingId,
        CancellationToken ct = default);
}
