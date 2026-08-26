using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

internal sealed class ChannelNyxIdAgentKeyReadinessPort(
    ISecretVault secretVault,
    NyxIdApiClient nyxClient,
    ILogger<ChannelNyxIdAgentKeyReadinessPort> logger)
    : IChannelNyxIdAgentKeyReadinessPort
{
    public async Task<ChannelNyxIdAgentKeyReadinessResult> EnsureReadyAsync(
        DurableCallerCredentialRef credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!IsExactChannelAgentKeyReference(credential))
            return ChannelNyxIdAgentKeyReadinessResult.Failed("channel_agent_key_reference_invalid");

        try
        {
            var resolved = await secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    credential.Ref,
                    credential.Purpose,
                    credential.OwnerScopeKey,
                    credential.SubjectId,
                    "channel-workflow-agent-key-readiness"),
                ct);
            if (!resolved.Resolved ||
                string.IsNullOrWhiteSpace(resolved.Secret) ||
                !MatchesExactDescriptor(credential, resolved.Reference))
            {
                return ChannelNyxIdAgentKeyReadinessResult.Failed("channel_agent_key_unavailable");
            }

            await ChannelNyxIdAgentKeyScopePolicy.EnsureProxyScopeAsync(
                nyxClient,
                resolved.Secret.Trim(),
                credential.SubjectId,
                logger,
                ct);
            return ChannelNyxIdAgentKeyReadinessResult.Succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (string.Equals(
            ex.Message,
            "channel_agent_key_credential_class_invalid",
            StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Channel Agent Key uses an incompatible NyxID credential class and must be reissued by its registration owner: subjectId={SubjectId} scope={Scope}",
                credential.SubjectId,
                credential.OwnerScopeKey);
            return ChannelNyxIdAgentKeyReadinessResult.Failed(
                "channel_agent_key_rebind_required");
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Channel Agent Key readiness failed before workflow start: subjectId={SubjectId} scope={Scope} failureType={FailureType}",
                credential.SubjectId,
                credential.OwnerScopeKey,
                ex.GetType().Name);
            return ChannelNyxIdAgentKeyReadinessResult.Failed("channel_agent_key_not_ready");
        }
    }

    private static bool IsExactChannelAgentKeyReference(DurableCallerCredentialRef credential) =>
        credential.SourceKind == DurableCallerCredentialSourceKind.ChannelRegistration &&
        !string.IsNullOrWhiteSpace(credential.Ref) &&
        string.Equals(
            credential.Purpose,
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(credential.OwnerScopeKey) &&
        !string.IsNullOrWhiteSpace(credential.SubjectId) &&
        credential.SecretReference is { } descriptor &&
        !string.IsNullOrWhiteSpace(descriptor.Ref);

    private static bool MatchesExactDescriptor(
        DurableCallerCredentialRef expected,
        SecretReference? actual)
    {
        var descriptor = expected.SecretReference;
        return descriptor is not null &&
               actual is not null &&
               string.Equals(actual.Ref, expected.Ref, StringComparison.Ordinal) &&
               string.Equals(actual.Purpose, expected.Purpose, StringComparison.Ordinal) &&
               string.Equals(actual.OwnerScopeKey, expected.OwnerScopeKey, StringComparison.Ordinal) &&
               string.Equals(actual.Ref, descriptor.Ref, StringComparison.Ordinal) &&
               string.Equals(actual.Purpose, descriptor.Purpose, StringComparison.Ordinal) &&
               string.Equals(actual.OwnerScopeKey, descriptor.OwnerScopeKey, StringComparison.Ordinal) &&
               string.Equals(actual.Fingerprint, descriptor.Fingerprint, StringComparison.Ordinal) &&
               actual.Version == descriptor.Version &&
               actual.CreatedAtUnixMs == descriptor.CreatedAtUnixMs &&
               actual.ExpiresAtUnixMs == descriptor.ExpiresAtUnixMs &&
               actual.Version > 0 &&
               !string.IsNullOrWhiteSpace(actual.Fingerprint) &&
               actual.CreatedAtUnixMs > 0 &&
               (actual.ExpiresAtUnixMs <= 0 ||
                actual.ExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}

internal static class ChannelNyxIdAgentKeyScopePolicy
{
    public const string ProvisionedScopes = "read write proxy";

    public static async Task EnsureProxyScopeAsync(
        NyxIdApiClient nyxClient,
        string credential,
        string apiKeyId,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nyxClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyId);

        var normalizedApiKeyId = apiKeyId.Trim();
        var currentResponse = await nyxClient.GetApiKeyAsync(credential, normalizedApiKeyId, ct);
        EnsureGeneralProxyCredentialClass(currentResponse);
        var currentScopes = ParseScopes(currentResponse, "scope_inspection_failed");
        if (HasProxyScope(currentScopes))
            return;

        var updatedScopes = AppendProxyScope(currentScopes);
        var updateResponse = await nyxClient.UpdateApiKeyAsync(
            credential,
            normalizedApiKeyId,
            JsonSerializer.Serialize(new { scopes = updatedScopes }),
            ct);
        var confirmedScopes = ParseScopes(updateResponse, "scope_update_failed");
        if (!HasProxyScope(confirmedScopes))
            throw Controlled("scope_update_not_confirmed");

        logger.LogInformation(
            "Upgraded channel Agent Key for NyxID workflow proxy access: apiKeyId={ApiKeyId}",
            normalizedApiKeyId);
    }

    private static string ParseScopes(string response, string failureCode)
    {
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw Controlled(failureCode);

        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("scopes", out var scopesElement) ||
                scopesElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(scopesElement.GetString()))
            {
                throw Controlled(failureCode);
            }

            return scopesElement.GetString()!.Trim();
        }
        catch (JsonException)
        {
            throw Controlled(failureCode);
        }
    }

    private static bool HasProxyScope(string scopes) =>
        scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(static scope => scope is "proxy" or "proxy:*");

    private static void EnsureGeneralProxyCredentialClass(string response)
    {
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
        {
            if (NyxApiResponseHelper.IsDurableGrantMismatchError(response))
                throw Controlled("credential_class_invalid");
            throw Controlled("credential_class_inspection_failed");
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            if (!NyxIdApiAccessResponseParser.TryParseCreatedAgentApiKeySecurityClass(
                    document.RootElement,
                    out var securityClass) ||
                securityClass is not { IsGeneralProxyCredential: true })
            {
                throw Controlled("credential_class_invalid");
            }
        }
        catch (JsonException)
        {
            throw Controlled("credential_class_inspection_failed");
        }
    }

    private static string AppendProxyScope(string scopes)
    {
        var values = scopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        values.Add("proxy");
        return string.Join(' ', values);
    }

    private static InvalidOperationException Controlled(string code) =>
        new($"channel_agent_key_{code}");
}
