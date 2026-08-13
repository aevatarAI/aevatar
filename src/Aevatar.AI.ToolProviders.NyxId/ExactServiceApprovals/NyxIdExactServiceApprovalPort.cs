using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.NyxId.ExactServiceApprovals;

public enum NyxIdExactServiceApprovalState
{
    Unspecified = 0,
    Pending,
    Approved,
    Denied,
    Expired,
    Revoked,
    Drifted,
    Redeeming,
    Redeemed,
    Failed,
}

public enum NyxIdExactServiceApprovalCreateDisposition
{
    Created = 0,
    TierAUnavailable,
    ApprovalNotRequired,
    Rejected,
}

public sealed record NyxIdExactServiceApprovalReceipt(
    int HttpStatus,
    string ResponseBody,
    string ResponseDigest);

public static class NyxIdExactServiceApprovalReceiptValidator
{
    public static bool IsSuccessfulHttpStatus(NyxIdExactServiceApprovalReceipt receipt) =>
        receipt.HttpStatus is >= 200 and <= 299;

    public static bool HasValidDigest(NyxIdExactServiceApprovalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ResponseDigest.Length != 71 ||
            !receipt.ResponseDigest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !IsLowerHex(receipt.ResponseDigest.AsSpan(7)))
        {
            return false;
        }

        var expected = ComputeDigest(receipt.ResponseBody);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(receipt.ResponseDigest));
    }

    public static string ComputeDigest(string responseBody) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(responseBody)))}";

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}

public sealed record NyxIdExactServiceApprovalSnapshot(
    NyxIdExactServiceApprovalState State,
    NyxIdExactServiceApprovalAuthority Authority,
    NyxIdExactServiceApprovalReceipt? Receipt = null,
    string? FailureCode = null);

public sealed record NyxIdExactServiceApprovalCreateResult(
    NyxIdExactServiceApprovalCreateDisposition Disposition,
    NyxIdExactServiceApprovalSnapshot? Snapshot = null,
    string? FailureCode = null);

public interface INyxIdExactServiceApprovalPort
{
    Task<NyxIdExactServiceApprovalCreateResult> CreateAsync(
        string accessToken,
        AgentToolOperationAdmission admission,
        JsonNode arguments,
        string operationId,
        long operationGeneration,
        string idempotencyKey,
        CancellationToken ct);

    Task<NyxIdExactServiceApprovalSnapshot> ObserveAsync(
        string accessToken,
        NyxIdExactServiceApprovalAuthority authority,
        CancellationToken ct);

    Task<NyxIdExactServiceApprovalSnapshot> DecideAsync(
        string accessToken,
        NyxIdExactServiceApprovalAuthority authority,
        bool approved,
        CancellationToken ct);

    Task<NyxIdExactServiceApprovalSnapshot> RedeemAsync(
        string accessToken,
        NyxIdExactServiceApprovalAuthority authority,
        CancellationToken ct);
}

public sealed class NyxIdExactServiceApprovalPort : INyxIdExactServiceApprovalPort
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly NyxIdApiClient _client;

    public NyxIdExactServiceApprovalPort(NyxIdApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<NyxIdExactServiceApprovalCreateResult> CreateAsync(
        string accessToken,
        AgentToolOperationAdmission admission,
        JsonNode arguments,
        string operationId,
        long operationGeneration,
        string idempotencyKey,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(arguments);
        if (admission.Identity is not AgentToolOperationIdentity.PublishedEndpoint endpoint)
            return new(NyxIdExactServiceApprovalCreateDisposition.Rejected,
                FailureCode: "exact_selector_required");

        var mapped = NyxIdExactServiceArgumentsMapper.Map(admission, arguments);
        if (!mapped.Succeeded)
        {
            return new(
                NyxIdExactServiceApprovalCreateDisposition.Rejected,
                FailureCode: SafeFailureCode(mapped.FailureCode!));
        }
        var exactArguments = mapped.Arguments!;

        var operationDigest = ComputeOperationDigest(
            admission.ServiceInstanceId,
            endpoint.EndpointId,
            admission.ContractDigest,
            exactArguments);
        var body = new JsonObject
        {
            ["user_service_id"] = admission.ServiceInstanceId,
            ["endpoint_id"] = endpoint.EndpointId,
            ["catalog_digest"] = admission.CatalogDigest,
            ["endpoint_contract_digest"] = admission.ContractDigest,
            ["operation_digest"] = operationDigest,
            ["operation_id"] = operationId,
            ["operation_generation"] = operationGeneration,
            ["idempotency_key"] = idempotencyKey,
            ["arguments"] = exactArguments.DeepClone(),
        }.ToJsonString();
        var response = await _client.CreateExactServiceApprovalRequestAsync(
            accessToken, body, ct).ConfigureAwait(false);
        if (TryReadError(response, out var status, out var errorText))
        {
            if (IsTierAUnavailable(status, errorText))
                return new(NyxIdExactServiceApprovalCreateDisposition.TierAUnavailable);
            if (status == 409 && errorText.Contains(
                    "exact_service_approval_not_required", StringComparison.Ordinal))
            {
                return new(NyxIdExactServiceApprovalCreateDisposition.ApprovalNotRequired);
            }
            return new(
                NyxIdExactServiceApprovalCreateDisposition.Rejected,
                FailureCode: SafeFailureCode(errorText));
        }
        var snapshot = ParseSnapshot(response);
        return snapshot is null || !MatchesCreate(
                snapshot.Authority,
                endpoint,
                admission,
                operationDigest,
                operationId,
                operationGeneration,
                idempotencyKey)
            ? new(NyxIdExactServiceApprovalCreateDisposition.Rejected,
                FailureCode: "invalid_exact_approval_response")
            : new(NyxIdExactServiceApprovalCreateDisposition.Created, snapshot);
    }

    public async Task<NyxIdExactServiceApprovalSnapshot> ObserveAsync(
        string accessToken,
        NyxIdExactServiceApprovalAuthority authority,
        CancellationToken ct)
    {
        ValidateAuthority(authority);
        var response = await _client.GetExactServiceApprovalStatusAsync(
            accessToken, authority.RequestId, ct).ConfigureAwait(false);
        var snapshot = ParseSnapshot(response);
        return snapshot is not null && MatchesAuthority(snapshot.Authority, authority)
            ? snapshot
            : Failed(authority, "exact_service_authority_mismatch");
    }

    public async Task<NyxIdExactServiceApprovalSnapshot> DecideAsync(
        string accessToken,
        NyxIdExactServiceApprovalAuthority authority,
        bool approved,
        CancellationToken ct)
    {
        ValidateAuthority(authority);
        var current = await ObserveAsync(accessToken, authority, ct).ConfigureAwait(false);
        if ((approved && current.State == NyxIdExactServiceApprovalState.Approved) ||
            (!approved && current.State == NyxIdExactServiceApprovalState.Denied) ||
            current.State is NyxIdExactServiceApprovalState.Expired or
                NyxIdExactServiceApprovalState.Revoked or
                NyxIdExactServiceApprovalState.Drifted or
                NyxIdExactServiceApprovalState.Redeeming or
                NyxIdExactServiceApprovalState.Redeemed or
                NyxIdExactServiceApprovalState.Failed)
        {
            return current;
        }
        if (current.State != NyxIdExactServiceApprovalState.Pending)
            return Failed(authority, "exact_service_decision_state_conflict");

        var response = await _client.DecideApprovalAsync(
            accessToken, authority.RequestId, approved, ct).ConfigureAwait(false);
        if (TryReadError(response, out _, out var errorText))
            return Failed(authority, errorText);
        return await ObserveAsync(accessToken, authority, ct).ConfigureAwait(false);
    }

    public async Task<NyxIdExactServiceApprovalSnapshot> RedeemAsync(
        string accessToken,
        NyxIdExactServiceApprovalAuthority authority,
        CancellationToken ct)
    {
        ValidateAuthority(authority);
        var body = new JsonObject
        {
            ["catalog_digest"] = authority.CatalogDigest,
            ["operation_digest"] = authority.OperationDigest,
            ["operation_id"] = authority.OperationId,
            ["operation_generation"] = authority.OperationGeneration,
            ["idempotency_key"] = authority.IdempotencyKey,
        }.ToJsonString();
        var response = await _client.RedeemExactServiceApprovalAsync(
            accessToken, authority.RequestId, body, ct).ConfigureAwait(false);
        var snapshot = ParseSnapshot(response);
        return snapshot is not null && MatchesAuthority(snapshot.Authority, authority)
            ? snapshot
            : Failed(authority, "exact_service_authority_mismatch");
    }

    public static string ComputeOperationDigest(
        string userServiceId,
        string endpointId,
        string endpointContractDigest,
        JsonNode arguments)
    {
        var canonical = Canonicalize(new JsonObject
        {
            ["contract_version"] = "nyxid-exact-operation.v1",
            ["user_service_id"] = userServiceId,
            ["endpoint_id"] = endpointId,
            ["endpoint_contract_digest"] = endpointContractDigest,
            ["arguments"] = arguments.DeepClone(),
        });
        var bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString(CanonicalJsonOptions));
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";
    }

    private static JsonNode Canonicalize(JsonNode node) => node switch
    {
        JsonObject value => new JsonObject(value
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(item => KeyValuePair.Create(
                item.Key,
                item.Value is null ? null : Canonicalize(item.Value)))),
        JsonArray value => new JsonArray(value
            .Select(item => item is null ? null : Canonicalize(item))
            .ToArray()),
        _ => node.DeepClone(),
    };

    private static NyxIdExactServiceApprovalSnapshot? ParseSnapshot(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "request_id", out var requestId) ||
                !TryString(root, "state", out var stateText) ||
                !TryString(root, "user_service_id", out var serviceId) ||
                !TryString(root, "endpoint_id", out var endpointId) ||
                !TryString(root, "catalog_digest", out var catalogDigest) ||
                !TryString(root, "endpoint_contract_digest", out var contractDigest) ||
                !TryString(root, "operation_digest", out var operationDigest) ||
                !TryString(root, "operation_id", out var operationId) ||
                !root.TryGetProperty("operation_generation", out var generation) ||
                !generation.TryGetInt64(out var operationGeneration) ||
                !TryString(root, "idempotency_key", out var idempotencyKey) ||
                !TryString(root, "expires_at", out var expiresAtText) ||
                !DateTimeOffset.TryParse(expiresAtText, out var expiresAt))
            {
                return null;
            }
            var authority = new NyxIdExactServiceApprovalAuthority
            {
                RequestId = requestId,
                UserServiceId = serviceId,
                EndpointId = endpointId,
                CatalogDigest = catalogDigest,
                EndpointContractDigest = contractDigest,
                OperationDigest = operationDigest,
                OperationId = operationId,
                OperationGeneration = operationGeneration,
                IdempotencyKey = idempotencyKey,
                ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
            };
            var state = MapState(stateText);
            var receipt = ParseReceipt(root);
            var failureCode = TryString(root, "failure_code", out var failure)
                ? failure
                : null;
            return ValidateSnapshotReceipt(state, authority, receipt, failureCode);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NyxIdExactServiceApprovalReceipt? ParseReceipt(JsonElement root)
    {
        if (!root.TryGetProperty("receipt", out var receipt) ||
            receipt.ValueKind != JsonValueKind.Object ||
            !receipt.TryGetProperty("http_status", out var httpStatus) ||
            !httpStatus.TryGetInt32(out var status) ||
            !TryString(receipt, "response_body", out var responseBody) ||
            !TryString(receipt, "response_digest", out var responseDigest))
        {
            return null;
        }
        return new(status, responseBody, responseDigest);
    }

    private static NyxIdExactServiceApprovalSnapshot ValidateSnapshotReceipt(
        NyxIdExactServiceApprovalState state,
        NyxIdExactServiceApprovalAuthority authority,
        NyxIdExactServiceApprovalReceipt? receipt,
        string? failureCode)
    {
        if (state != NyxIdExactServiceApprovalState.Redeemed)
            return new(state, authority, receipt, failureCode);
        if (receipt is null)
        {
            return new(
                NyxIdExactServiceApprovalState.Failed,
                authority,
                FailureCode: "exact_service_receipt_missing");
        }
        if (!NyxIdExactServiceApprovalReceiptValidator.HasValidDigest(receipt))
        {
            return new(
                NyxIdExactServiceApprovalState.Failed,
                authority,
                FailureCode: "exact_service_receipt_digest_mismatch");
        }
        return NyxIdExactServiceApprovalReceiptValidator.IsSuccessfulHttpStatus(receipt)
            ? new(state, authority, receipt, failureCode)
            : new(
                NyxIdExactServiceApprovalState.Failed,
                authority,
                FailureCode: "exact_service_provider_http_error");
    }

    private static NyxIdExactServiceApprovalSnapshot Failed(
        NyxIdExactServiceApprovalAuthority authority,
        string response) => new(
        NyxIdExactServiceApprovalState.Failed,
        authority.Clone(),
        FailureCode: SafeFailureCode(response));

    private static bool MatchesCreate(
        NyxIdExactServiceApprovalAuthority authority,
        AgentToolOperationIdentity.PublishedEndpoint endpoint,
        AgentToolOperationAdmission admission,
        string operationDigest,
        string operationId,
        long operationGeneration,
        string idempotencyKey) =>
        string.Equals(authority.UserServiceId, admission.ServiceInstanceId, StringComparison.Ordinal) &&
        string.Equals(authority.EndpointId, endpoint.EndpointId, StringComparison.Ordinal) &&
        string.Equals(authority.CatalogDigest, admission.CatalogDigest, StringComparison.Ordinal) &&
        string.Equals(authority.EndpointContractDigest, admission.ContractDigest, StringComparison.Ordinal) &&
        string.Equals(authority.OperationDigest, operationDigest, StringComparison.Ordinal) &&
        string.Equals(authority.OperationId, operationId, StringComparison.Ordinal) &&
        authority.OperationGeneration == operationGeneration &&
        string.Equals(authority.IdempotencyKey, idempotencyKey, StringComparison.Ordinal);

    private static bool MatchesAuthority(
        NyxIdExactServiceApprovalAuthority left,
        NyxIdExactServiceApprovalAuthority right) =>
        string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal) &&
        string.Equals(left.UserServiceId, right.UserServiceId, StringComparison.Ordinal) &&
        string.Equals(left.EndpointId, right.EndpointId, StringComparison.Ordinal) &&
        string.Equals(left.CatalogDigest, right.CatalogDigest, StringComparison.Ordinal) &&
        string.Equals(left.EndpointContractDigest, right.EndpointContractDigest, StringComparison.Ordinal) &&
        string.Equals(left.OperationDigest, right.OperationDigest, StringComparison.Ordinal) &&
        string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) &&
        left.OperationGeneration == right.OperationGeneration &&
        string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal) &&
        Equals(left.ExpiresAt, right.ExpiresAt);

    private static bool TryReadError(string response, out int status, out string errorText)
    {
        status = 0;
        errorText = response;
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var marker) ||
                marker.ValueKind != JsonValueKind.True)
            {
                return false;
            }
            if (root.TryGetProperty("status", out var statusElement))
                statusElement.TryGetInt32(out status);
            if (root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
                errorText = body.GetString() ?? response;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTierAUnavailable(int status, string errorText)
    {
        if (status == 405)
            return true;
        if (status != 404)
            return false;

        var body = errorText.Trim();
        return body.Length == 0 ||
               string.Equals(body, "Not Found", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateAuthority(NyxIdExactServiceApprovalAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (string.IsNullOrWhiteSpace(authority.RequestId) ||
            string.IsNullOrWhiteSpace(authority.OperationId) ||
            authority.OperationGeneration < 1 ||
            string.IsNullOrWhiteSpace(authority.IdempotencyKey))
        {
            throw new ArgumentException("Exact-service approval authority is incomplete.", nameof(authority));
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }
        value = property.GetString()!;
        return true;
    }

    private static NyxIdExactServiceApprovalState MapState(string state) => state switch
    {
        "pending" => NyxIdExactServiceApprovalState.Pending,
        "approved" => NyxIdExactServiceApprovalState.Approved,
        "denied" => NyxIdExactServiceApprovalState.Denied,
        "expired" => NyxIdExactServiceApprovalState.Expired,
        "revoked" => NyxIdExactServiceApprovalState.Revoked,
        "drifted" => NyxIdExactServiceApprovalState.Drifted,
        "redeeming" => NyxIdExactServiceApprovalState.Redeeming,
        "redeemed" => NyxIdExactServiceApprovalState.Redeemed,
        "failed" => NyxIdExactServiceApprovalState.Failed,
        _ => NyxIdExactServiceApprovalState.Unspecified,
    };

    private static string SafeFailureCode(string value)
    {
        var normalized = string.Concat(value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        normalized = string.Join('_', normalized.Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
            return "exact_service_approval_failed";
        return normalized.Length <= 96 ? normalized : normalized[..96];
    }
}

public sealed class UnavailableNyxIdExactServiceApprovalPort : INyxIdExactServiceApprovalPort
{
    public Task<NyxIdExactServiceApprovalCreateResult> CreateAsync(
        string accessToken,
        AgentToolOperationAdmission admission,
        JsonNode arguments,
        string operationId,
        long operationGeneration,
        string idempotencyKey,
        CancellationToken ct) =>
        Task.FromResult(new NyxIdExactServiceApprovalCreateResult(
            NyxIdExactServiceApprovalCreateDisposition.TierAUnavailable));

    public Task<NyxIdExactServiceApprovalSnapshot> ObserveAsync(
        string accessToken,
        NyxIdExactServiceApprovalAuthority authority,
        CancellationToken ct) => Task.FromResult(Unavailable(authority));

    public Task<NyxIdExactServiceApprovalSnapshot> DecideAsync(
        string accessToken,
        NyxIdExactServiceApprovalAuthority authority,
        bool approved,
        CancellationToken ct) => Task.FromResult(Unavailable(authority));

    public Task<NyxIdExactServiceApprovalSnapshot> RedeemAsync(
        string accessToken,
        NyxIdExactServiceApprovalAuthority authority,
        CancellationToken ct) => Task.FromResult(Unavailable(authority));

    private static NyxIdExactServiceApprovalSnapshot Unavailable(
        NyxIdExactServiceApprovalAuthority authority) => new(
        NyxIdExactServiceApprovalState.Failed,
        authority.Clone(),
        FailureCode: "exact_service_approval_unavailable");
}
