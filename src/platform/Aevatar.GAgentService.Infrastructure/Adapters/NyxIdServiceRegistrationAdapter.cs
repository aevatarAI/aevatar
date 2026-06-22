using System.Net.Http;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Models;
using Aevatar.GAgentService.Core.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class NyxIdServiceRegistrationAdapter : INyxIdServiceRegistrationPort
{
    private readonly NyxIdApiClient _client;
    private readonly ILogger<NyxIdServiceRegistrationAdapter> _logger;

    public NyxIdServiceRegistrationAdapter(
        NyxIdApiClient client,
        ILogger<NyxIdServiceRegistrationAdapter>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<NyxIdServiceRegistrationAdapter>.Instance;
    }

    public async Task<NyxIdServiceRegistrationResult> RegisterAsync(
        NyxIdServiceRegistrationRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var body = BuildServiceBody(request);
            var response = await _client.CreateServiceAsync(request.AccessToken, body, ct);
            return ParseRegistrationResponse(response, request.DesiredSpecHash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NyxID service registration failed for {ServiceKey}.", ServiceKeys.Build(request.Identity));
            return NyxIdServiceRegistrationResult.Failed(ClassifyException(ex), IsConflict(ex));
        }
    }

    public async Task<NyxIdServiceRegistrationResult> UpdateAsync(
        NyxIdServiceRegistrationRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ExistingNyxIdServiceId))
            return await RegisterAsync(request, ct);

        try
        {
            var body = BuildServiceBody(request);
            var response = await _client.UpdateServiceAsync(
                request.AccessToken,
                request.ExistingNyxIdServiceId,
                body,
                ct);
            return ParseRegistrationResponse(response, request.DesiredSpecHash)
                with { NyxIdServiceId = request.ExistingNyxIdServiceId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NyxID service update failed for {ServiceKey}.", ServiceKeys.Build(request.Identity));
            return NyxIdServiceRegistrationResult.Failed(ClassifyException(ex), IsConflict(ex));
        }
    }

    public async Task<NyxIdServiceLookupResult> GetAsync(
        NyxIdServiceLookupRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetServiceAsync(request.AccessToken, request.NyxIdServiceId, ct);
            return ParseLookupResponse(response, request.NyxIdServiceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NyxID service lookup failed for {ServiceId}.", request.NyxIdServiceId);
            var failure = ClassifyException(ex);
            return failure.Kind == NyxIdRegistrationFailureKind.NotFound
                ? NyxIdServiceLookupResult.Missing()
                : NyxIdServiceLookupResult.Failed(failure);
        }
    }

    public async Task<NyxIdServiceRetirementResult> RetireAsync(
        NyxIdServiceRetirementRequest request,
        CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteServiceAsync(request.AccessToken, request.NyxIdServiceId, ct);
            return NyxIdServiceRetirementResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NyxID service retirement failed for {ServiceId}.", request.NyxIdServiceId);
            var failure = ClassifyException(ex);
            return failure.Kind == NyxIdRegistrationFailureKind.NotFound
                ? NyxIdServiceRetirementResult.Success()
                : NyxIdServiceRetirementResult.Failed(failure);
        }
    }

    private static string BuildServiceBody(NyxIdServiceRegistrationRequest request)
    {
        var slug = BuildServiceSlug(request);
        var payload = new Dictionary<string, object?>
        {
            ["service_slug"] = slug,
            ["slug"] = slug,
            ["label"] = string.IsNullOrWhiteSpace(request.DisplayName) ? slug : request.DisplayName.Trim(),
            ["endpoint_url"] = request.OpenApiUrl.Trim(),
            ["openapi_spec_url"] = request.OpenApiUrl.Trim(),
            ["openapi_url"] = request.OpenApiUrl.Trim(),
            ["credential"] = request.ServiceCredential?.Trim() ?? string.Empty,
            ["forward_access_token"] = false,
            ["aevatar_desired_spec_hash"] = request.DesiredSpecHash.Trim(),
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildServiceSlug(NyxIdServiceRegistrationRequest request)
    {
        var candidate = string.IsNullOrWhiteSpace(request.ExistingNyxIdSlug)
            ? request.Identity.ServiceId
            : request.ExistingNyxIdSlug;
        var normalized = new string((candidate ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        normalized = string.Join('-', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized)
            ? ServiceKeys.Build(request.Identity).Replace(':', '-').ToLowerInvariant()
            : normalized;
    }

    private static NyxIdServiceRegistrationResult ParseRegistrationResponse(
        string response,
        string desiredSpecHash)
    {
        if (TryParseError(response, out var failure, out var conflict))
            return NyxIdServiceRegistrationResult.Failed(failure, conflict);

        var root = ParseRoot(response);
        var id = ReadString(root, "id", "service_id", "key_id") ?? string.Empty;
        var slug = ReadString(root, "slug", "service_slug") ?? string.Empty;
        var hash = ReadString(root, "aevatar_desired_spec_hash", "desired_spec_hash", "registered_spec_hash") ??
                   desiredSpecHash;
        return NyxIdServiceRegistrationResult.Success(id, slug, hash);
    }

    private static NyxIdServiceLookupResult ParseLookupResponse(string response, string fallbackId)
    {
        if (TryParseError(response, out var failure, out _))
            return failure.Kind == NyxIdRegistrationFailureKind.NotFound
                ? NyxIdServiceLookupResult.Missing()
                : NyxIdServiceLookupResult.Failed(failure);

        var root = ParseRoot(response);
        var id = ReadString(root, "id", "service_id", "key_id") ?? fallbackId;
        var slug = ReadString(root, "slug", "service_slug") ?? string.Empty;
        var hash = ReadString(root, "aevatar_desired_spec_hash", "desired_spec_hash", "registered_spec_hash") ?? string.Empty;
        return NyxIdServiceLookupResult.Success(id, slug, hash);
    }

    private static JsonElement ParseRoot(string response)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(response) ? "{}" : response);
        return document.RootElement.Clone();
    }

    private static bool TryParseError(
        string response,
        out NyxIdRegistrationFailure failure,
        out bool conflict)
    {
        failure = new NyxIdRegistrationFailure(NyxIdRegistrationFailureKind.Unspecified, string.Empty, false);
        conflict = false;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var status = ReadInt(root, "status", "code");
            var error = ReadString(root, "error", "message", "detail", "body");
            if (status is null && string.IsNullOrWhiteSpace(error))
                return false;

            if (status is 0 or 200 or 201)
                return false;

            failure = Classify(status ?? 0, error ?? "nyxid_error");
            conflict = failure.Kind == NyxIdRegistrationFailureKind.Conflict;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static NyxIdRegistrationFailure ClassifyException(Exception ex)
    {
        if (ex is HttpRequestException http && http.StatusCode.HasValue)
            return Classify((int)http.StatusCode.Value, http.Message);

        return new NyxIdRegistrationFailure(
            NyxIdRegistrationFailureKind.Transient,
            ex.GetType().Name,
            Retryable: true);
    }

    private static bool IsConflict(Exception ex) =>
        ex is HttpRequestException http && http.StatusCode == System.Net.HttpStatusCode.Conflict;

    private static NyxIdRegistrationFailure Classify(int status, string reason)
    {
        var kind = status switch
        {
            400 => NyxIdRegistrationFailureKind.Validation,
            401 or 403 => NyxIdRegistrationFailureKind.Unauthorized,
            404 => NyxIdRegistrationFailureKind.NotFound,
            409 => NyxIdRegistrationFailureKind.Conflict,
            >= 500 => NyxIdRegistrationFailureKind.Transient,
            _ => NyxIdRegistrationFailureKind.Adapter,
        };
        var retryable = kind is NyxIdRegistrationFailureKind.Transient or NyxIdRegistrationFailureKind.Conflict;
        return new NyxIdRegistrationFailure(kind, Redact(reason), retryable);
    }

    private static string Redact(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
            return "nyxid_error";
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            return ReadString(data, names);
        }

        return null;
    }

    private static int? ReadInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return number;
        }

        return null;
    }
}
