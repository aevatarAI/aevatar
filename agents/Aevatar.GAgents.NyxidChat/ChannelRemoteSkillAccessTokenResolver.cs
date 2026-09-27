using Aevatar.AI.Abstractions;
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
        var resolution = await ChannelRegistrationAgentKeySecretResolver.ResolveDetailedAsync(
                context,
                _secretVault,
                "channel-default-skill",
                ct)
            .ConfigureAwait(false);
        if (resolution.AgentKey is not null)
            return RemoteSkillAccessTokenResolution.Resolved(resolution.AgentKey);

        _logger.LogWarning(
            "Channel registration Agent Key remote-skill credential unavailable: registration={RegistrationId} reason={Reason}",
            context.Channel.BotRegistrationId ?? string.Empty,
            resolution.FailureReason);
        return RemoteSkillAccessTokenResolution.Failed(RemoteSkillAccessTokenFailureKind.Unavailable);
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
        CancellationToken ct) =>
        (await ResolveDetailedAsync(context, secretVault, auditReason, ct).ConfigureAwait(false)).AgentKey;

    public static async Task<ChannelRegistrationAgentKeySecretResolution> ResolveDetailedAsync(
        AgentToolExecutionContext context,
        ISecretVault? secretVault,
        string auditReason,
        CancellationToken ct)
    {
        var credential = context.Channel.WorkflowResultDeliveryCredential;
        var registrationId = Normalize(context.Channel.BotRegistrationId);
        var scopeId = Normalize(context.Channel.RegistrationScopeId);
        var subjectId = Normalize(credential?.SubjectId);
        var reference = credential?.SecretReference;
        var failureReason = ValidateInputs(
            secretVault,
            credential,
            registrationId,
            scopeId,
            subjectId,
            reference,
            context.ExecutionOwner);
        if (failureReason is not null)
            return ChannelRegistrationAgentKeySecretResolution.Failed(failureReason);

        var resolved = await secretVault!.ResolveAsync(
            new ResolveSecretRequest(
                reference!.Ref,
                CredentialSecretPurposes.ChannelNyxIdAgentKey,
                scopeId!,
                subjectId!,
                auditReason),
            ct).ConfigureAwait(false);
        if (!resolved.Resolved)
            return ChannelRegistrationAgentKeySecretResolution.Failed("vault_not_resolved");
        if (string.IsNullOrWhiteSpace(resolved.Secret))
            return ChannelRegistrationAgentKeySecretResolution.Failed("vault_secret_empty");
        if (resolved.Reference is not { } resolvedReference)
            return ChannelRegistrationAgentKeySecretResolution.Failed("vault_reference_missing");
        if (!MatchesReference(reference!, resolvedReference))
            return ChannelRegistrationAgentKeySecretResolution.Failed("vault_reference_mismatch");

        return ChannelRegistrationAgentKeySecretResolution.Resolved(resolved.Secret.Trim());
    }

    private static string? ValidateInputs(
        ISecretVault? secretVault,
        ChannelWorkflowResultDeliveryCredential? credential,
        string? registrationId,
        string? scopeId,
        string? subjectId,
        SecretReference? reference,
        AgentToolExecutionOwner owner)
    {
        if (secretVault is null)
            return "secret_vault_missing";
        if (credential is null)
            return "workflow_delivery_credential_missing";
        if (registrationId is null)
            return "registration_id_missing";
        if (scopeId is null)
            return "registration_scope_missing";
        if (owner.Kind != AgentToolExecutionOwnerKind.ChannelRegistration)
            return "execution_owner_kind_mismatch";
        if (!string.Equals(owner.OwnerId, registrationId, StringComparison.Ordinal))
            return "execution_owner_id_mismatch";
        if (subjectId is null)
            return "agent_key_subject_missing";
        if (reference is null)
            return "secret_reference_missing";
        if (string.IsNullOrWhiteSpace(reference.Ref))
            return "secret_reference_ref_missing";
        if (!string.Equals(reference.Purpose, CredentialSecretPurposes.ChannelNyxIdAgentKey, StringComparison.Ordinal))
            return "secret_reference_purpose_mismatch";
        if (!string.Equals(reference.OwnerScopeKey, scopeId, StringComparison.Ordinal))
            return "secret_reference_owner_scope_mismatch";
        return null;
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

internal sealed record ChannelRegistrationAgentKeySecretResolution(
    string? AgentKey,
    string FailureReason)
{
    public static ChannelRegistrationAgentKeySecretResolution Resolved(string agentKey) =>
        new(agentKey, string.Empty);

    public static ChannelRegistrationAgentKeySecretResolution Failed(string reason) =>
        new(null, reason);
}
