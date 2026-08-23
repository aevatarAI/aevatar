using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Resolves the transient token used by the remote-skill tools
/// (<c>use_skill</c>, <c>ornn_search_skills</c>). Bound turns use only the
/// verified sender token or a capability issued for the exact typed NyxID
/// authority; they never fall through to ambient bot-owner credentials.
/// Failures stay typed so the caller can tell the user whether to bind,
/// re-bind, or simply retry.
/// </summary>
public sealed class ChannelRemoteSkillAccessTokenResolver : IRemoteSkillAccessTokenResolver
{
    private readonly INyxIdSkillCapabilityIssuer? _capabilityIssuer;
    private readonly ILogger _logger;

    public ChannelRemoteSkillAccessTokenResolver(
        INyxIdSkillCapabilityIssuer? capabilityIssuer = null,
        ILogger<ChannelRemoteSkillAccessTokenResolver>? logger = null)
    {
        _capabilityIssuer = capabilityIssuer;
        _logger = logger ?? NullLogger<ChannelRemoteSkillAccessTokenResolver>.Instance;
    }

    public async Task<RemoteSkillAccessTokenResolution> ResolveAsync(string skillName, CancellationToken ct = default)
    {
        var context = AgentToolRequestContext.Current;
        var bindingId = Normalize(context?.SenderBinding.BindingId);
        if (bindingId is null)
        {
            var sourceToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context?.Credentials);
            return sourceToken is null
                ? RemoteSkillAccessTokenResolution.Failed(RemoteSkillAccessTokenFailureKind.ChannelBindingRequired)
                : RemoteSkillAccessTokenResolution.Resolved(sourceToken);
        }

        var senderToken = Normalize(context!.Credentials.SenderNyxIdAccessToken);
        if (senderToken is not null)
            return RemoteSkillAccessTokenResolution.Resolved(senderToken);

        if (_capabilityIssuer is null || !TryBuildSubject(context, out var subject))
            return RemoteSkillAccessTokenResolution.Failed(RemoteSkillAccessTokenFailureKind.Unavailable);

        try
        {
            var capability = await _capabilityIssuer
                .IssueByBindingIdAsync(subject, bindingId, ct)
                .ConfigureAwait(false);
            return RemoteSkillAccessTokenResolution.FromAccessToken(capability.AccessToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsBindingStateFailure(ex))
        {
            _logger.LogWarning(
                "NyxID remote-skill capability rejected the sender binding. skill={SkillName}, platform={Platform}, failure={FailureType}",
                Normalize(skillName) ?? "unknown",
                subject.Platform,
                ex.GetType().Name);
            return RemoteSkillAccessTokenResolution.Failed(
                RemoteSkillAccessTokenFailureKind.ChannelBindingRefreshRequired);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "NyxID remote-skill capability issue failed. skill={SkillName}, platform={Platform}, failure={FailureType}",
                Normalize(skillName) ?? "unknown",
                subject.Platform,
                ex.GetType().Name);
            return RemoteSkillAccessTokenResolution.Failed(RemoteSkillAccessTokenFailureKind.Unavailable);
        }
    }

    private static bool IsBindingStateFailure(Exception ex) =>
        ex is BindingRevokedException
            or BindingNotFoundException
            or BindingScopeMismatchException
            or BindingServiceAccessMismatchException;

    private static bool TryBuildSubject(
        AgentToolExecutionContext context,
        out ExternalSubjectRef subject)
    {
        subject = new ExternalSubjectRef();
        var authority = context.NyxIdAuthority;
        var platform = Normalize(authority.Platform);
        var externalUserId = Normalize(authority.ExternalUserId);
        if (platform is null || externalUserId is null)
            return false;

        subject = new ExternalSubjectRef
        {
            Platform = platform.ToLowerInvariant(),
            Tenant = Normalize(authority.Tenant) ?? string.Empty,
            ExternalUserId = externalUserId,
        };
        return true;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
