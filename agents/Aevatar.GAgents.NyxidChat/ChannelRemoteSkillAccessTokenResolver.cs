using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Resolves the transient token used by <c>use_skill</c>. Bound turns use only
/// the verified sender token or a capability issued for the exact typed NyxID
/// authority; they never fall through to ambient bot-owner credentials.
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

    public async Task<string?> ResolveAsync(string skillName, CancellationToken ct = default)
    {
        var context = AgentToolRequestContext.Current;
        var bindingId = Normalize(context?.SenderBinding.BindingId);
        if (bindingId is null)
            return AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context?.Credentials);

        var senderToken = Normalize(context!.Credentials.SenderNyxIdAccessToken);
        if (senderToken is not null)
            return senderToken;

        if (_capabilityIssuer is null || !TryBuildSubject(context, out var subject))
            return null;

        try
        {
            var capability = await _capabilityIssuer
                .IssueByBindingIdAsync(subject, bindingId, ct)
                .ConfigureAwait(false);
            return Normalize(capability.AccessToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "NyxID remote-skill capability issue failed. skill={SkillName}, platform={Platform}, failure={FailureType}",
                Normalize(skillName) ?? "unknown",
                subject.Platform,
                ex.GetType().Name);
            return null;
        }
    }

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
