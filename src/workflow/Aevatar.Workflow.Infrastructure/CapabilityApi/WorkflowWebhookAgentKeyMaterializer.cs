using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal interface IWorkflowWebhookAgentKeyMaterializer
{
    Task<WorkflowWebhookAgentKeyMaterializationResult> MaterializeAsync(
        WorkflowCallerNyxIdAuthority callerAuthority,
        WorkflowCapabilityAdmissionPlan admissionPlan,
        string scopeId,
        string routeKey,
        CancellationToken ct);

    Task<bool> RevokeAsync(
        WorkflowCallerNyxIdAuthority? callerAuthority,
        DurableCallerCredentialRef credential,
        string auditReason,
        CancellationToken ct);
}

internal sealed record WorkflowWebhookAgentKeyMaterializationResult(
    DurableCallerCredentialRef? Credential,
    int StatusCode,
    string ErrorCode)
{
    public bool Succeeded => Credential is not null;

    public static WorkflowWebhookAgentKeyMaterializationResult Success(
        DurableCallerCredentialRef credential) =>
        new(credential, StatusCodes.Status200OK, string.Empty);

    public static WorkflowWebhookAgentKeyMaterializationResult Failure(
        string errorCode,
        int statusCode) =>
        new(null, statusCode, errorCode);
}

internal sealed class WorkflowWebhookAgentKeyMaterializer : IWorkflowWebhookAgentKeyMaterializer
{
    private const int CredentialNameDigestBytes = 12;
    private static readonly JsonSerializerOptions CreateKeyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IWorkflowCallerAccessTokenProvider _accessTokenProvider;
    private readonly INyxIdApiClientFactory _nyxIdApiClientFactory;
    private readonly ISecretVault _secretVault;
    private readonly ILogger<WorkflowWebhookAgentKeyMaterializer>? _logger;

    public WorkflowWebhookAgentKeyMaterializer(
        IWorkflowCallerAccessTokenProvider accessTokenProvider,
        INyxIdApiClientFactory nyxIdApiClientFactory,
        ISecretVault secretVault,
        ILogger<WorkflowWebhookAgentKeyMaterializer>? logger = null)
    {
        _accessTokenProvider = accessTokenProvider ??
            throw new ArgumentNullException(nameof(accessTokenProvider));
        _nyxIdApiClientFactory = nyxIdApiClientFactory ??
            throw new ArgumentNullException(nameof(nyxIdApiClientFactory));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _logger = logger;
    }

    public async Task<WorkflowWebhookAgentKeyMaterializationResult> MaterializeAsync(
        WorkflowCallerNyxIdAuthority callerAuthority,
        WorkflowCapabilityAdmissionPlan admissionPlan,
        string scopeId,
        string routeKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(callerAuthority);
        ArgumentNullException.ThrowIfNull(admissionPlan);
        if (!TryResolveRequiredServiceIds(admissionPlan, callerAuthority, out var serviceIds))
        {
            return WorkflowWebhookAgentKeyMaterializationResult.Failure(
                "WEBHOOK_CALLER_CREDENTIAL_SCOPE_INVALID",
                StatusCodes.Status409Conflict);
        }

        var bearerToken = await IssueManagementBearerAsync(callerAuthority, ct);
        if (bearerToken is null)
        {
            return WorkflowWebhookAgentKeyMaterializationResult.Failure(
                "WEBHOOK_CALLER_CREDENTIAL_ISSUANCE_UNAVAILABLE",
                StatusCodes.Status503ServiceUnavailable);
        }

        var client = _nyxIdApiClientFactory.CreateClient();
        string scopePlanResponse;
        try
        {
            scopePlanResponse = await client.PlanApiKeyScopeAsync(
                bearerToken,
                serviceIds,
                targetOrganizationId: null,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NyxID webhook Agent Key scope planning failed.");
            return WorkflowWebhookAgentKeyMaterializationResult.Failure(
                "WEBHOOK_CALLER_CREDENTIAL_SCOPE_PLAN_UNAVAILABLE",
                StatusCodes.Status503ServiceUnavailable);
        }

        var parsedScopePlan = NyxIdApiAccessResponseParser.ParseScopePlan(scopePlanResponse);
        if (!parsedScopePlan.Succeeded)
        {
            return WorkflowWebhookAgentKeyMaterializationResult.Failure(
                "WEBHOOK_CALLER_CREDENTIAL_SCOPE_PLAN_FAILED",
                NormalizeProviderStatus(parsedScopePlan.Failure?.HttpStatus));
        }

        var scopePlan = parsedScopePlan.Value!;
        if (!ScopePlanMatches(scopePlan, callerAuthority, serviceIds))
        {
            return WorkflowWebhookAgentKeyMaterializationResult.Failure(
                "WEBHOOK_CALLER_CREDENTIAL_SCOPE_CHANGED",
                StatusCodes.Status409Conflict);
        }

        string createResponse;
        try
        {
            createResponse = await client.CreateApiKeyAsync(
                bearerToken,
                JsonSerializer.Serialize(new
                {
                    name = BuildCredentialName(scopeId, routeKey, callerAuthority.ExternalUserId),
                    scopes = "proxy",
                    platform = "generic",
                    allow_all_services = false,
                    allow_all_nodes = false,
                    allowed_service_ids = scopePlan.AllowedServiceIds,
                    allowed_node_ids = scopePlan.AllowedNodeIds,
                    scope_plan_digest = scopePlan.NormalizedGrantDigest,
                }, CreateKeyJsonOptions),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "NyxID webhook Agent Key creation failed.");
            return WorkflowWebhookAgentKeyMaterializationResult.Failure(
                "WEBHOOK_CALLER_CREDENTIAL_CREATE_UNAVAILABLE",
                StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryReadIssuedCredential(
                createResponse,
                out var providerCredentialId,
                out var fullKey,
                out var createStatus))
        {
            return WorkflowWebhookAgentKeyMaterializationResult.Failure(
                "WEBHOOK_CALLER_CREDENTIAL_CREATE_FAILED",
                NormalizeProviderStatus(createStatus));
        }

        try
        {
            var stored = await _secretVault.PutAsync(new StoreSecretRequest(
                CredentialSecretPurposes.WorkflowWebhookBindingAgentKey,
                scopeId,
                callerAuthority.ExternalUserId,
                fullKey,
                "workflow-webhook-binding-agent-key"), ct);
            return WorkflowWebhookAgentKeyMaterializationResult.Success(new DurableCallerCredentialRef
            {
                Ref = stored.Reference.Ref,
                Purpose = stored.Reference.Purpose,
                OwnerScopeKey = stored.Reference.OwnerScopeKey,
                SubjectId = callerAuthority.ExternalUserId,
                SourceKind = DurableCallerCredentialSourceKind.WebhookBinding,
                SecretReference = stored.Reference.Clone(),
                ProviderCredentialId = providerCredentialId,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryDeleteProviderCredentialAsync(
                client,
                bearerToken,
                providerCredentialId,
                CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Webhook Agent Key vault persistence failed.");
            await TryDeleteProviderCredentialAsync(
                client,
                bearerToken,
                providerCredentialId,
                CancellationToken.None);
            return WorkflowWebhookAgentKeyMaterializationResult.Failure(
                "WEBHOOK_CALLER_CREDENTIAL_VAULT_UNAVAILABLE",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    public async Task<bool> RevokeAsync(
        WorkflowCallerNyxIdAuthority? callerAuthority,
        DurableCallerCredentialRef credential,
        string auditReason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!IsWebhookVaultReference(credential))
            return false;

        var providerRevoked = string.IsNullOrWhiteSpace(credential.ProviderCredentialId);
        if (!providerRevoked &&
            callerAuthority is not null &&
            string.Equals(
                callerAuthority.ExternalUserId,
                credential.SubjectId,
                StringComparison.Ordinal))
        {
            var bearerToken = await IssueManagementBearerAsync(callerAuthority, ct);
            if (bearerToken is not null)
            {
                providerRevoked = await TryDeleteProviderCredentialAsync(
                    _nyxIdApiClientFactory.CreateClient(),
                    bearerToken,
                    credential.ProviderCredentialId,
                    ct);
            }
        }

        var vaultRevoked = false;
        try
        {
            var result = await _secretVault.RevokeAsync(new RevokeSecretRequest(
                credential.Ref,
                credential.Purpose,
                credential.OwnerScopeKey,
                credential.SubjectId,
                auditReason), ct);
            vaultRevoked = result.Revoked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Webhook Agent Key vault revocation failed.");
        }

        if (!providerRevoked || !vaultRevoked)
        {
            _logger?.LogError(
                "Webhook Agent Key cleanup was incomplete. providerRevoked={ProviderRevoked} vaultRevoked={VaultRevoked}",
                providerRevoked,
                vaultRevoked);
        }
        return providerRevoked && vaultRevoked;
    }

    private async Task<string?> IssueManagementBearerAsync(
        WorkflowCallerNyxIdAuthority callerAuthority,
        CancellationToken ct)
    {
        try
        {
            var issued = await _accessTokenProvider.IssueAsync(callerAuthority, ct);
            var parsed = WorkflowCallerCredentialTokens.ParseOptional(issued);
            return parsed.IsValid ? parsed.NormalizedBearerToken : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Webhook Agent Key management bearer issuance failed.");
            return null;
        }
    }

    private async Task<bool> TryDeleteProviderCredentialAsync(
        NyxIdApiClient client,
        string bearerToken,
        string providerCredentialId,
        CancellationToken ct)
    {
        try
        {
            var response = await client.DeleteApiKeyAsync(bearerToken, providerCredentialId, ct);
            if (string.IsNullOrWhiteSpace(response))
                return true;
            return !TryReadErrorEnvelope(response, out var status) || status == StatusCodes.Status404NotFound;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "NyxID webhook Agent Key revocation failed.");
            return false;
        }
    }

    private static bool TryResolveRequiredServiceIds(
        WorkflowCapabilityAdmissionPlan plan,
        WorkflowCallerNyxIdAuthority callerAuthority,
        out IReadOnlyList<string> serviceIds)
    {
        serviceIds = [];
        if (plan.ExecutionMode != ExternalCapabilityExecutionMode.Durable ||
            !WorkflowCapabilityAdmissionPlanIntegrity.IsCanonicalDurableAuthorizationOwner(
                plan.DurableAuthorizationOwner) ||
            !string.Equals(
                plan.DurableAuthorizationOwner.OwnerSubject,
                callerAuthority.ExternalUserId,
                StringComparison.Ordinal) ||
            !string.Equals(
                plan.AdmissionDigest,
                WorkflowCapabilityAdmissionPlanIntegrity.ComputeAdmissionDigest(plan),
                StringComparison.Ordinal))
        {
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var admission in plan.InvocationAdmissions)
            {
                WorkflowCapabilityAdmissionPlanIntegrity
                    .ValidateInvocationAdmissionIntrinsicIntegrity(admission);
                var serviceId = admission.Capability.CapabilityCase switch
                {
                    ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService =>
                        admission.Capability.NyxIdUserService.UserServiceId,
                    ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest =>
                        admission.Capability.NyxIdUserRequest.Request?.UserServiceId,
                    ExternalWorkflowCapabilityRef.CapabilityOneofCase.CodeExecution =>
                        admission.Capability.CodeExecution.UserServiceId,
                    _ => null,
                };
                if (serviceId is null)
                    continue;
                if (string.IsNullOrWhiteSpace(serviceId) ||
                    !string.Equals(serviceId, serviceId.Trim(), StringComparison.Ordinal))
                {
                    return false;
                }
                ids.Add(serviceId);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }

        serviceIds = ids.Order(StringComparer.Ordinal).ToArray();
        return serviceIds.Count > 0;
    }

    private static bool ScopePlanMatches(
        NyxIdApiKeyScopePlan scopePlan,
        WorkflowCallerNyxIdAuthority callerAuthority,
        IReadOnlyList<string> serviceIds) =>
        scopePlan.AuthenticatedActor.Kind == NyxIdScopePlanPrincipalKind.Personal &&
        scopePlan.IntendedKeyOwner.Kind == NyxIdScopePlanPrincipalKind.Personal &&
        string.Equals(
            scopePlan.AuthenticatedActor.Id,
            callerAuthority.ExternalUserId,
            StringComparison.Ordinal) &&
        string.Equals(
            scopePlan.IntendedKeyOwner.Id,
            callerAuthority.ExternalUserId,
            StringComparison.Ordinal) &&
        scopePlan.AllowedServiceIds.SequenceEqual(serviceIds, StringComparer.Ordinal);

    private static bool TryReadIssuedCredential(
        string response,
        out string providerCredentialId,
        out string fullKey,
        out int? status)
    {
        providerCredentialId = string.Empty;
        fullKey = string.Empty;
        if (TryReadErrorEnvelope(response, out status))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            providerCredentialId = ReadNormalizedString(document.RootElement, "id") ?? string.Empty;
            fullKey = ReadNormalizedString(document.RootElement, "full_key") ?? string.Empty;
            return providerCredentialId.Length > 0 && fullKey.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadErrorEnvelope(string? response, out int? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(response))
            return true;
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            {
                return false;
            }
            status = root.TryGetProperty("status", out var statusValue) &&
                     statusValue.TryGetInt32(out var parsedStatus)
                ? parsedStatus
                : null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsWebhookVaultReference(DurableCallerCredentialRef credential) =>
        credential.SourceKind == DurableCallerCredentialSourceKind.WebhookBinding &&
        DurableCallerAgentKeyContract.Matches(credential.SourceKind, credential.Purpose) &&
        !string.IsNullOrWhiteSpace(credential.Ref) &&
        !string.IsNullOrWhiteSpace(credential.OwnerScopeKey) &&
        !string.IsNullOrWhiteSpace(credential.SubjectId);

    private static string BuildCredentialName(
        string scopeId,
        string routeKey,
        string subjectId)
    {
        var identity = string.Join("\0", scopeId, routeKey, subjectId);
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity))
                    .AsSpan(0, CredentialNameDigestBytes))
            .ToLowerInvariant();
        return "aevatar-webhook-" + digest;
    }

    private static string? ReadNormalizedString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ||
               !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? null
            : value;
    }

    private static int NormalizeProviderStatus(int? status) =>
        status is >= 400 and <= 599
            ? status.Value
            : StatusCodes.Status502BadGateway;
}
