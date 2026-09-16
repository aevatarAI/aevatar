using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

public enum ChannelWorkflowScopeAuthorizationFailure
{
    None = 0,
    RegistrationScopeMissing = 1,
    SenderSubjectMissing = 2,
    BindingUnavailable = 3,
    OwnerScopeMismatch = 4,
    AuthorityUnavailable = 5,
}

public sealed record ChannelWorkflowAuthorizedScopeResolution(
    string AuthorizedScopeId,
    ChannelWorkflowScopeAuthorizationFailure Failure)
{
    public bool IsAuthorized =>
        Failure == ChannelWorkflowScopeAuthorizationFailure.None &&
        AuthorizedScopeId.Length != 0;

    public static ChannelWorkflowAuthorizedScopeResolution Authorized(string scopeId) =>
        new(scopeId, ChannelWorkflowScopeAuthorizationFailure.None);

    public static ChannelWorkflowAuthorizedScopeResolution Denied(
        ChannelWorkflowScopeAuthorizationFailure failure) =>
        new(string.Empty, failure);
}

public interface IChannelWorkflowAuthorizedScopeResolver
{
    Task<ChannelWorkflowAuthorizedScopeResolution> ResolveAsync(
        ExternalSubjectRef? senderSubject,
        string? registrationScopeId,
        CancellationToken ct = default);
}

public sealed class ChannelWorkflowAuthorizedScopeResolver : IChannelWorkflowAuthorizedScopeResolver
{
    private readonly IOwnerScopeResolver? _ownerScopeResolver;
    private readonly ILogger<ChannelWorkflowAuthorizedScopeResolver> _logger;

    public ChannelWorkflowAuthorizedScopeResolver(
        IOwnerScopeResolver? ownerScopeResolver = null,
        ILogger<ChannelWorkflowAuthorizedScopeResolver>? logger = null)
    {
        _ownerScopeResolver = ownerScopeResolver;
        _logger = logger ?? NullLogger<ChannelWorkflowAuthorizedScopeResolver>.Instance;
    }

    public async Task<ChannelWorkflowAuthorizedScopeResolution> ResolveAsync(
        ExternalSubjectRef? senderSubject,
        string? registrationScopeId,
        CancellationToken ct = default)
    {
        var normalizedRegistrationScopeId = NormalizeOptional(registrationScopeId);
        if (normalizedRegistrationScopeId is null)
        {
            return ChannelWorkflowAuthorizedScopeResolution.Denied(
                ChannelWorkflowScopeAuthorizationFailure.RegistrationScopeMissing);
        }

        if (senderSubject is null ||
            string.IsNullOrWhiteSpace(senderSubject.Platform) ||
            string.IsNullOrWhiteSpace(senderSubject.ExternalUserId))
        {
            return ChannelWorkflowAuthorizedScopeResolution.Denied(
                ChannelWorkflowScopeAuthorizationFailure.SenderSubjectMissing);
        }

        if (_ownerScopeResolver is null)
        {
            return ChannelWorkflowAuthorizedScopeResolution.Denied(
                ChannelWorkflowScopeAuthorizationFailure.AuthorityUnavailable);
        }

        OwnerScopeId? ownerScope;
        try
        {
            ownerScope = await _ownerScopeResolver.ResolveAsync(senderSubject, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Workflow scope authorization failed to resolve sender owner scope: platform={Platform}, tenant={Tenant}, sender={Sender}",
                senderSubject.Platform,
                senderSubject.Tenant,
                senderSubject.ExternalUserId);
            return ChannelWorkflowAuthorizedScopeResolution.Denied(
                ChannelWorkflowScopeAuthorizationFailure.AuthorityUnavailable);
        }

        var normalizedOwnerScopeId = NormalizeOptional(ownerScope?.Value);
        if (normalizedOwnerScopeId is null)
        {
            return ChannelWorkflowAuthorizedScopeResolution.Denied(
                ChannelWorkflowScopeAuthorizationFailure.BindingUnavailable);
        }

        if (!string.Equals(normalizedOwnerScopeId, normalizedRegistrationScopeId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Workflow scope authorization rejected a cross-owner request: registrationScope={RegistrationScope}, ownerScope={OwnerScope}, platform={Platform}, tenant={Tenant}, sender={Sender}",
                normalizedRegistrationScopeId,
                normalizedOwnerScopeId,
                senderSubject.Platform,
                senderSubject.Tenant,
                senderSubject.ExternalUserId);
            return ChannelWorkflowAuthorizedScopeResolution.Denied(
                ChannelWorkflowScopeAuthorizationFailure.OwnerScopeMismatch);
        }

        return ChannelWorkflowAuthorizedScopeResolution.Authorized(normalizedRegistrationScopeId);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
