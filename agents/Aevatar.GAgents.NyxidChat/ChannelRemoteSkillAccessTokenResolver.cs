using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Resolves the transient token used by the remote-skill tools
/// (<c>use_skill</c>, <c>ornn_search_skills</c>). Explicit sender-triggered
/// turns use only the verified sender token or a capability issued for the
/// exact typed NyxID authority. A registration default-skill turn uses only
/// the registration's validated Channel Agent Key. Neither path falls through
/// to an unrelated ambient credential. Failures stay typed so the caller can
/// tell the user whether to bind, re-bind, or simply retry.
/// </summary>
public sealed class ChannelRemoteSkillAccessTokenResolver : IRemoteSkillAccessTokenResolver
{
    private readonly INyxIdSkillCapabilityIssuer? _capabilityIssuer;
    private readonly ISecretVault? _secretVault;
    private readonly ILogger _logger;

    public ChannelRemoteSkillAccessTokenResolver(
        INyxIdSkillCapabilityIssuer? capabilityIssuer = null,
        ISecretVault? secretVault = null,
        ILogger<ChannelRemoteSkillAccessTokenResolver>? logger = null)
    {
        _capabilityIssuer = capabilityIssuer;
        _secretVault = secretVault;
        _logger = logger ?? NullLogger<ChannelRemoteSkillAccessTokenResolver>.Instance;
    }

    public async Task<RemoteSkillAccessTokenResolution> ResolveAsync(string skillName, CancellationToken ct = default)
    {
        var context = AgentToolRequestContext.Current;
        if (IsChannelDefaultSkillInvocation(context, skillName))
            return await ResolveChannelRegistrationAgentKeyAsync(context!, ct).ConfigureAwait(false);

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

    private async Task<RemoteSkillAccessTokenResolution> ResolveChannelRegistrationAgentKeyAsync(
        AgentToolExecutionContext context,
        CancellationToken ct)
    {
        var agentKey = await ChannelRegistrationAgentKeySecretResolver.ResolveAsync(
                context,
                _secretVault,
                "channel-default-skill",
                ct)
            .ConfigureAwait(false);
        return agentKey is null
            ? RemoteSkillAccessTokenResolution.Failed(RemoteSkillAccessTokenFailureKind.Unavailable)
            : RemoteSkillAccessTokenResolution.Resolved(agentKey);
    }

    private static bool IsChannelDefaultSkillInvocation(AgentToolExecutionContext? context, string skillName)
    {
        var recovery = context?.SkillRecovery;
        var requested = Normalize(skillName);
        return recovery?.FromChannelDefaultSkillBinding == true &&
               requested is not null &&
               string.Equals(requested, Normalize(recovery.PrimarySkillName), StringComparison.Ordinal) &&
               string.Equals(requested, Normalize(recovery.CommandName), StringComparison.Ordinal);
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

internal static class ChannelRegistrationAgentKeySecretResolver
{
    public static async Task<string?> ResolveAsync(
        AgentToolExecutionContext context,
        ISecretVault? secretVault,
        string auditReason,
        CancellationToken ct)
    {
        var credential = context.Channel.WorkflowResultDeliveryCredential;
        var registrationId = Normalize(context.Channel.BotRegistrationId);
        var scopeId = Normalize(context.Channel.RegistrationScopeId);
        var subjectId = Normalize(credential?.SubjectId);
        if (secretVault is null || credential is null || registrationId is null || scopeId is null ||
            context.ExecutionOwner.Kind != AgentToolExecutionOwnerKind.ChannelRegistration ||
            !string.Equals(context.ExecutionOwner.OwnerId, registrationId, StringComparison.Ordinal) ||
            subjectId is null ||
            credential.SecretReference is not { } reference ||
            string.IsNullOrWhiteSpace(reference.Ref) ||
            !string.Equals(reference.Purpose, CredentialSecretPurposes.ChannelNyxIdAgentKey, StringComparison.Ordinal) ||
            !string.Equals(reference.OwnerScopeKey, scopeId, StringComparison.Ordinal))
        {
            return null;
        }

        var resolved = await secretVault.ResolveAsync(
            new ResolveSecretRequest(
                reference.Ref,
                CredentialSecretPurposes.ChannelNyxIdAgentKey,
                scopeId,
                subjectId,
                auditReason),
            ct).ConfigureAwait(false);
        if (!resolved.Resolved || string.IsNullOrWhiteSpace(resolved.Secret) ||
            resolved.Reference is not { } resolvedReference ||
            !MatchesReference(reference, resolvedReference))
        {
            return null;
        }

        return resolved.Secret.Trim();
    }

    private static bool MatchesReference(SecretReference expected, SecretReference actual) =>
        string.Equals(actual.Ref, expected.Ref, StringComparison.Ordinal) &&
        string.Equals(actual.Purpose, expected.Purpose, StringComparison.Ordinal) &&
        string.Equals(actual.OwnerScopeKey, expected.OwnerScopeKey, StringComparison.Ordinal) &&
        string.Equals(actual.Fingerprint, expected.Fingerprint, StringComparison.Ordinal) &&
        actual.Version == expected.Version &&
        actual.CreatedAtUnixMs == expected.CreatedAtUnixMs &&
        actual.ExpiresAtUnixMs == expected.ExpiresAtUnixMs;

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
