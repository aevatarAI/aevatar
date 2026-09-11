using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class ChannelAgentKeyProvisioningService(
    NyxIdApiClient nyxClient,
    ISecretVault secretVault,
    ILogger<ChannelAgentKeyProvisioningService> logger,
    ChannelAgentKeyWriteMode writeMode = ChannelAgentKeyWriteMode.Disabled)
{
    private const string NyxRelayApiKeyPlatform = "generic";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    public Task<ChannelAgentKeyCredential> ProvisionAsync(
        string platform,
        string accessToken,
        string relayCallbackUrl,
        string scopeId,
        string registrationId,
        VerifiedChannelRegistrationOwner owner,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return ProvisionCoreAsync(
            platform,
            accessToken,
            relayCallbackUrl,
            scopeId,
            registrationId,
            owner,
            null,
            ct);
    }

    public Task<ChannelAgentKeyCredential> ProvisionAsync(
        string platform,
        string accessToken,
        string relayCallbackUrl,
        string scopeId,
        string registrationId,
        VerifiedChannelRegistrationExplicitAuthorization authorization,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return ProvisionCoreAsync(
            platform,
            accessToken,
            relayCallbackUrl,
            scopeId,
            registrationId,
            null,
            authorization,
            ct);
    }

    private async Task<ChannelAgentKeyCredential> ProvisionCoreAsync(
        string platform,
        string accessToken,
        string relayCallbackUrl,
        string scopeId,
        string registrationId,
        VerifiedChannelRegistrationOwner? owner,
        VerifiedChannelRegistrationExplicitAuthorization? authorization,
        CancellationToken ct)
    {
        if (writeMode != ChannelAgentKeyWriteMode.NyxIdDefault)
            throw new InvalidOperationException("channel_agent_key_write_gate_closed");

        var normalizedPlatform = NormalizePlatform(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(relayCallbackUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);

        var normalizedScopeId = scopeId.Trim();
        var normalizedRegistrationId = registrationId.Trim();
        if (authorization is null &&
            (owner is null ||
             !IsValidVerifiedOwner(owner) ||
             !string.Equals(owner.KeyOwner.Id, normalizedScopeId, StringComparison.Ordinal)) ||
            authorization is not null &&
            !string.Equals(authorization.Plan.KeyOwner.Id, normalizedScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("service_owner_forbidden");
        }

        var nameSuffixLength = Math.Min(12, normalizedRegistrationId.Length);
        var requestJson = authorization is null
            ? BuildDefaultRequest(
                normalizedPlatform,
                normalizedRegistrationId[..nameSuffixLength],
                relayCallbackUrl.Trim(),
                owner!.TargetOrganizationId)
            : BuildExplicitRequest(normalizedPlatform, normalizedRegistrationId[..nameSuffixLength], relayCallbackUrl.Trim(), authorization);
        string response;
        try
        {
            response = await nyxClient.CreateApiKeyAsync(accessToken, requestJson, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("provisioning_failed");
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(ct);
        }

        if (authorization is not null && IsScopePlanChanged(response))
            throw new InvalidOperationException("scope_plan_changed");

        var cleanupApiKeyId = TryReadApiKeyIdForCleanup(response);
        ParsedChannelAgentKey parsed;
        try
        {
            parsed = ParseCreatedKey(response, authorization);
        }
        catch (Exception ex) when (ex is JsonException or ChannelAgentKeyContractException)
        {
            if (cleanupApiKeyId is not null)
                await CleanupApiKeyDetachedAsync(accessToken, cleanupApiKeyId);
            throw new InvalidOperationException("channel_authorization_contract_invalid");
        }

        SecretReference? storedReference = null;
        try
        {
            var stored = await parsed.StoreAsync(
                secretVault,
                new StoreSecretRequest(
                    CredentialSecretPurposes.ChannelNyxIdAgentKey,
                    normalizedScopeId,
                    parsed.ApiKeyId,
                    string.Empty,
                    $"{normalizedPlatform}-channel-agent-key-provisioning:{normalizedRegistrationId}"),
                ct);
            storedReference = stored.Reference;
            if (!IsCompleteStoredReference(storedReference, normalizedScopeId))
                throw new InvalidOperationException("vault_descriptor_invalid");
        }
        catch (Exception ex)
        {
            await CleanupAfterVaultFailureAsync(
                accessToken,
                parsed.ApiKeyId,
                storedReference,
                normalizedRegistrationId);
            logger.LogWarning(
                "Channel Agent Key Vault storage failed: registration={RegistrationId}, apiKeyId={ApiKeyId}, failureType={FailureType}",
                normalizedRegistrationId,
                parsed.ApiKeyId,
                ex.GetType().Name);
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new InvalidOperationException("secret_vault_unavailable");
        }

        var credential = new ChannelAgentKeyCredential
        {
            ApiKeyId = parsed.ApiKeyId,
            SecretReference = storedReference.Clone(),
            Grant = parsed.Grant.Clone(),
        };
        var validationCommand = new ChannelBotRegisterCommand
        {
            ScopeId = normalizedScopeId,
            NyxAgentApiKeyId = credential.ApiKeyId,
            WorkflowResultDeliveryCredential = credential.SecretReference.Clone(),
            ChannelAgentKey = credential.Clone(),
            AuthorizationMode = authorization is null
                ? ChannelRegistrationAuthorizationMode.NyxidDefault
                : ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist,
        };
        if (authorization is not null)
        {
            validationCommand.RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist();
            validationCommand.RegistrationServiceAllowlist.ServiceIds.Add(authorization.Plan.RegistrationServiceIds);
        }
        if (!ChannelRegistrationAuthorizationContract.IsValidNewCommand(validationCommand))
        {
            await CleanupDetachedAsync(
                accessToken,
                credential,
                normalizedRegistrationId);
            throw new InvalidOperationException("secret_vault_unavailable");
        }

        return credential;
    }

    public async Task CleanupAsync(
        string accessToken,
        ChannelAgentKeyCredential credential,
        string registrationId,
        CancellationToken cleanupToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationId);

        if (!string.IsNullOrWhiteSpace(credential.ApiKeyId))
        {
            await NyxApiResponseHelper.TryRollbackAsync(
                () => nyxClient.DeleteApiKeyAsync(
                    accessToken,
                    credential.ApiKeyId,
                    cleanupToken),
                "api_key",
                credential.ApiKeyId,
                logger);
        }

        var reference = credential.SecretReference;
        if (reference is null || string.IsNullOrWhiteSpace(reference.Ref))
            return;

        try
        {
            var result = await secretVault.RevokeAsync(
                new RevokeSecretRequest(
                    reference.Ref,
                    reference.Purpose,
                    reference.OwnerScopeKey,
                    credential.ApiKeyId,
                    $"channel-agent-key-provisioning-rollback:{registrationId.Trim()}"),
                cleanupToken);
            if (!result.Revoked)
            {
                logger.LogWarning(
                    "Channel Agent Key Vault revoke did not remove a record during provisioning rollback: registration={RegistrationId}, apiKeyId={ApiKeyId}, failureCode={FailureCode}",
                    registrationId.Trim(),
                    credential.ApiKeyId,
                    "vault_revoke_failed");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Channel Agent Key Vault revoke failed during provisioning rollback: registration={RegistrationId}, apiKeyId={ApiKeyId}, failureCode={FailureCode}, failureType={FailureType}",
                registrationId.Trim(),
                credential.ApiKeyId,
                "vault_revoke_failed",
                ex.GetType().Name);
        }
    }

    private async Task CleanupAfterVaultFailureAsync(
        string accessToken,
        string apiKeyId,
        SecretReference? reference,
        string registrationId)
    {
        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        if (reference is null)
        {
            await NyxApiResponseHelper.TryRollbackAsync(
                () => nyxClient.DeleteApiKeyAsync(accessToken, apiKeyId, cleanupCts.Token),
                "api_key",
                apiKeyId,
                logger);
            return;
        }

        await CleanupAsync(
            accessToken,
            new ChannelAgentKeyCredential
            {
                ApiKeyId = apiKeyId,
                SecretReference = reference.Clone(),
            },
            registrationId,
            cleanupCts.Token);
    }

    private async Task CleanupApiKeyDetachedAsync(string accessToken, string apiKeyId)
    {
        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        await NyxApiResponseHelper.TryRollbackAsync(
            () => nyxClient.DeleteApiKeyAsync(accessToken, apiKeyId, cleanupCts.Token),
            "api_key",
            apiKeyId,
            logger);
    }

    private async Task CleanupDetachedAsync(
        string accessToken,
        ChannelAgentKeyCredential credential,
        string registrationId)
    {
        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        await CleanupAsync(accessToken, credential, registrationId, cleanupCts.Token);
    }

    private static string BuildExplicitRequest(
        string platform, string registrationSuffix, string callbackUrl,
        VerifiedChannelRegistrationExplicitAuthorization authorization)
    {
        var plan = authorization.Plan;
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("name", $"aevatar-{platform}-relay-{registrationSuffix}");
            writer.WriteString("scopes", ChannelNyxIdAgentKeyScopePolicy.ProvisionedScopes);
            writer.WriteString("platform", NyxRelayApiKeyPlatform);
            writer.WriteString("callback_url", callbackUrl);
            writer.WriteBoolean("allow_all_services", false);
            writer.WriteBoolean("allow_all_nodes", false);
            writer.WriteStartArray("allowed_service_ids");
            foreach (var id in plan.AllowedServiceIds)
                writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteStartArray("allowed_node_ids");
            foreach (var id in plan.AllowedNodeIds)
                writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteString("scope_plan_digest", plan.ScopePlanDigest);
            if (plan.TargetOrganizationId is not null)
                writer.WriteString("target_org_id", plan.TargetOrganizationId);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static string BuildDefaultRequest(
        string platform,
        string registrationSuffix,
        string callbackUrl,
        string? targetOrganizationId)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("name", $"aevatar-{platform}-relay-{registrationSuffix}");
            writer.WriteString("scopes", ChannelNyxIdAgentKeyScopePolicy.ProvisionedScopes);
            writer.WriteString("platform", NyxRelayApiKeyPlatform);
            writer.WriteString("callback_url", callbackUrl);
            if (targetOrganizationId is not null)
                writer.WriteString("target_org_id", targetOrganizationId);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool IsValidVerifiedOwner(VerifiedChannelRegistrationOwner owner)
    {
        if (!IsCanonicalIdentifier(owner.AuthenticatedActorId) ||
            !IsCanonicalIdentifier(owner.KeyOwner.Id))
        {
            return false;
        }

        return owner.KeyOwner.Kind switch
        {
            ChannelRegistrationKeyOwnerKind.Personal =>
                owner.TargetOrganizationId is null &&
                string.Equals(
                    owner.AuthenticatedActorId,
                    owner.KeyOwner.Id,
                    StringComparison.Ordinal),
            ChannelRegistrationKeyOwnerKind.Organization =>
                string.Equals(
                    owner.TargetOrganizationId,
                    owner.KeyOwner.Id,
                    StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsCanonicalIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool IsScopePlanChanged(string response)
    {
        if (!NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            return false;
        try
        {
            using var envelope = JsonDocument.Parse(response);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var envelopeError) || envelopeError.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number ||
                !status.TryGetInt32(out var httpStatus) || httpStatus != 409 ||
                !root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String)
                return false;
            using var provider = JsonDocument.Parse(body.GetString()!);
            return provider.RootElement.ValueKind == JsonValueKind.Object &&
                provider.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String &&
                error.GetString() is "scope_plan_changed" or "api_key_scope_plan_stale";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ParsedChannelAgentKey ParseCreatedKey(
        string response, VerifiedChannelRegistrationExplicitAuthorization? authorization)
    {
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            throw new ChannelAgentKeyContractException();

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
            !NyxIdApiAccessResponseParser.TryParseCreatedAgentApiKeySecurityClass(
                root,
                out var securityClass) ||
            securityClass is not { IsGeneralProxyCredential: true })
        {
            throw new ChannelAgentKeyContractException();
        }

        var apiKeyId = RequireCanonicalString(root, "id");
        var fullKey = RequireSecret(root, "full_key");
        var scopes = RequireCanonicalString(root, "scopes");
        if (!string.Equals(
                scopes,
                ChannelNyxIdAgentKeyScopePolicy.ProvisionedScopes,
                StringComparison.Ordinal))
        {
            throw new ChannelAgentKeyContractException();
        }

        var allowAllServices = RequireBoolean(root, "allow_all_services");
        var allowAllNodes = RequireBoolean(root, "allow_all_nodes");
        var allowedServiceIds = RequireCanonicalIdentifiers(root, "allowed_service_ids");
        var allowedNodeIds = RequireCanonicalIdentifiers(root, "allowed_node_ids");
        if (allowAllServices && allowedServiceIds.Count != 0 ||
            allowAllNodes && allowedNodeIds.Count != 0)
        {
            throw new ChannelAgentKeyContractException();
        }

        var grant = new ChannelAgentKeyGrantSnapshot
        {
            AllowAllServices = allowAllServices,
            AllowAllNodes = allowAllNodes,
        };
        grant.AllowedServiceIds.Add(allowedServiceIds);
        grant.AllowedNodeIds.Add(allowedNodeIds);
        if (authorization is not null)
        {
            var plan = authorization.Plan;
            if (allowAllServices || allowAllNodes ||
                !allowedServiceIds.SequenceEqual(plan.AllowedServiceIds, StringComparer.Ordinal) ||
                !allowedNodeIds.SequenceEqual(plan.AllowedNodeIds, StringComparer.Ordinal))
                throw new ChannelAgentKeyContractException();
            // NyxID creation does not echo the digest. The verified plan is its only source.
            grant.ScopePlanDigest = plan.ScopePlanDigest;
        }
        return new ParsedChannelAgentKey(apiKeyId, fullKey, grant);
    }

    private static string? TryReadApiKeyIdForCleanup(string response)
    {
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
            return null;

        try
        {
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Count(property => property.NameEquals("id")) != 1 ||
                !document.RootElement.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = id.GetString();
            return string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl)
                ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RequireCanonicalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw new ChannelAgentKeyContractException();
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ChannelAgentKeyContractException();
        }

        return value;
    }

    private static string RequireSecret(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new ChannelAgentKeyContractException();
        }

        return property.GetString()!;
    }

    private static bool RequireBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ChannelAgentKeyContractException();
        }

        return property.GetBoolean();
    }

    private static IReadOnlyList<string> RequireCanonicalIdentifiers(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            throw new ChannelAgentKeyContractException();
        }

        var values = new List<string>();
        string? previous = null;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ChannelAgentKeyContractException();
            var value = item.GetString();
            if (string.IsNullOrWhiteSpace(value) ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                value.Any(char.IsControl) ||
                previous is not null && string.CompareOrdinal(previous, value) >= 0)
            {
                throw new ChannelAgentKeyContractException();
            }

            values.Add(value);
            previous = value;
        }

        return values;
    }

    private static bool IsCompleteStoredReference(SecretReference? reference, string scopeId) =>
        reference is not null &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            StringComparison.Ordinal) &&
        string.Equals(reference.OwnerScopeKey, scopeId, StringComparison.Ordinal) &&
        reference.Version > 0 &&
        !string.IsNullOrWhiteSpace(reference.Fingerprint) &&
        reference.CreatedAtUnixMs > 0 &&
        reference.ExpiresAtUnixMs >= 0;

    private static string NormalizePlatform(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        var normalized = platform.Trim().ToLowerInvariant();
        return normalized is "lark" or "telegram"
            ? normalized
            : throw new ArgumentException("Unsupported channel platform.", nameof(platform));
    }

    private sealed class ParsedChannelAgentKey(
        string apiKeyId,
        string fullKey,
        ChannelAgentKeyGrantSnapshot grant)
    {
        private readonly string _fullKey = fullKey;

        public string ApiKeyId { get; } = apiKeyId;
        public ChannelAgentKeyGrantSnapshot Grant { get; } = grant;

        public Task<StoreSecretResult> StoreAsync(
            ISecretVault vault,
            StoreSecretRequest request,
            CancellationToken ct) =>
            vault.PutAsync(request with { Secret = _fullKey }, ct);

        public override string ToString() =>
            $"{nameof(ParsedChannelAgentKey)} {{ ApiKeyId = {ApiKeyId}, FullKey = [redacted] }}";
    }

    private sealed class ChannelAgentKeyContractException : Exception;
}
