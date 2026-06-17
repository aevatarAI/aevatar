using System.Globalization;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdWorkflowConnectedServiceFileSubmitAdapter(NyxIdApiClient client)
    : IWorkflowConnectedServiceFileSubmitAdapter
{
    public const string ProviderName = "nyxid_connected_service";

    private readonly NyxIdApiClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Provider => ProviderName;

    public IReadOnlyList<WorkflowConnectedServiceFileSubmitTarget> Targets { get; } = [];

    public async ValueTask<WorkflowConnectedServiceFileSubmitResult> SubmitAsync(
        WorkflowConnectedServiceFileSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Target.Endpoint == null)
        {
            return new WorkflowConnectedServiceFileSubmitResult(
                Succeeded: false,
                Error: "missing_endpoint",
                Detail: "Connected-service file submit endpoint is not configured.");
        }

        var token = Normalize(request.CallerCredential.BearerToken);
        if (token == null)
        {
            return new WorkflowConnectedServiceFileSubmitResult(
                Succeeded: false,
                Error: "missing_bearer",
                Detail: "Connected-service file submit requires a workflow caller bearer token.");
        }

        string response;
        try
        {
            response = await _client.ProxyRequestMultipartAsync(
                token,
                request.Target.Endpoint.ServiceSlug,
                request.Target.Endpoint.Path,
                request.Target.Endpoint.Method,
                request.Target.Endpoint.Body ?? EmptyFields,
                request.Target.Endpoint.FileFieldName,
                request.FileName,
                request.MediaType,
                request.Content,
                request.Target.Endpoint.Headers == null
                    ? null
                    : new Dictionary<string, string>(request.Target.Endpoint.Headers, StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WorkflowConnectedServiceFileSubmitResult(
                Succeeded: false,
                Error: "provider_call_failed",
                Detail: "Connected-service file submit failed.");
        }

        if (TryParseProviderError(response, out var providerError))
        {
            return new WorkflowConnectedServiceFileSubmitResult(
                Succeeded: false,
                Error: providerError.Error,
                Detail: providerError.Detail,
                Code: providerError.Code);
        }

        var outputCode = TryReadOutputCode(response, request.Target.OutputField);
        return new WorkflowConnectedServiceFileSubmitResult(
            Succeeded: true,
            OutputCode: outputCode,
            Code: TryReadTopLevelCode(response));
    }

    private static string? TryReadOutputCode(string response, string outputField)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var data = root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object
                ? dataProp
                : root;
            return TryReadString(data, outputField) ?? TryReadString(root, outputField);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseProviderError(string? response, out NyxIdFileSubmitProviderError error)
    {
        error = new NyxIdFileSubmitProviderError("provider_error", "empty_provider_response", null);
        if (string.IsNullOrWhiteSpace(response))
            return true;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.True)
            {
                var status = TryReadInt(root, "status");
                var detail = $"nyx_proxy_error status={status?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
                error = new NyxIdFileSubmitProviderError("nyx_proxy_error", detail, status);
                return true;
            }

            if (root.TryGetProperty("code", out var codeProp) &&
                codeProp.ValueKind == JsonValueKind.Number &&
                codeProp.TryGetInt32(out var code) &&
                code != 0)
            {
                error = new NyxIdFileSubmitProviderError(
                    "provider_error",
                    $"provider_code={code.ToString(CultureInfo.InvariantCulture)}",
                    code);
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            error = new NyxIdFileSubmitProviderError("invalid_provider_response", "invalid_provider_response_json", null);
            return true;
        }
    }

    private static string? TryReadString(JsonElement source, string name)
    {
        return source.ValueKind == JsonValueKind.Object &&
               source.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;
    }

    private static int? TryReadTopLevelCode(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            return TryReadInt(document.RootElement, "code");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TryReadInt(JsonElement source, string name)
    {
        return source.ValueKind == JsonValueKind.Object &&
               source.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly IReadOnlyDictionary<string, string> EmptyFields =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private sealed record NyxIdFileSubmitProviderError(string Error, string Detail, int? Code);
}
