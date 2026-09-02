using System.Globalization;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdWorkflowFileMultipartUploadPort(NyxIdApiClient client)
    : IWorkflowFileMultipartUploadPort
{
    private const int MaxOutputCodeLength = 512;

    private readonly NyxIdApiClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask<WorkflowFileMultipartUploadResult> UploadAsync(
        WorkflowFileMultipartUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = Normalize(request.CallerCredential.BearerToken);
        if (token == null)
        {
            return WorkflowFileMultipartUploadResult.Failure(
                "missing_bearer",
                "workflow_file_submit requires a workflow caller bearer token.");
        }

        string response;
        try
        {
            response = await _client.ProxyRequestMultipartAsync(
                token,
                request.ServiceSlug,
                request.Path,
                request.Method,
                request.FormFields,
                request.FileFieldName,
                request.FileName,
                request.MediaType,
                request.Content,
                extraHeaders: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WorkflowFileMultipartUploadResult.Failure(
                "provider_call_failed",
                "workflow_file_submit multipart upload failed.");
        }

        if (TryParseProviderError(response, out var providerError))
        {
            return WorkflowFileMultipartUploadResult.Failure(
                providerError.Error,
                providerError.Detail,
                providerError.ProviderCode,
                providerError.HttpStatus);
        }

        return ReadProviderSuccess(response, request.OutputSelector);
    }

    private static bool TryParseProviderError(
        string? response,
        out NyxIdMultipartUploadProviderError error)
    {
        error = new NyxIdMultipartUploadProviderError(
            "provider_error",
            "workflow_file_submit provider returned an error envelope.",
            null,
            null);
        if (string.IsNullOrWhiteSpace(response))
        {
            error = new NyxIdMultipartUploadProviderError(
                "invalid_provider_response",
                "workflow_file_submit provider response was empty.",
                null,
                null);
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = new NyxIdMultipartUploadProviderError(
                    "invalid_provider_response",
                    "workflow_file_submit provider response root was not a JSON object.",
                    null,
                    null);
                return true;
            }

            if (root.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.True)
            {
                error = new NyxIdMultipartUploadProviderError(
                    "provider_error",
                    "workflow_file_submit provider returned an error envelope.",
                    TryReadInt(root, "code"),
                    TryReadInt(root, "status"));
                return true;
            }

            if (root.TryGetProperty("code", out var codeProp) &&
                codeProp.ValueKind == JsonValueKind.Number &&
                codeProp.TryGetInt32(out var code) &&
                code != 0)
            {
                error = new NyxIdMultipartUploadProviderError(
                    "provider_error",
                    $"provider_code={code.ToString(CultureInfo.InvariantCulture)}",
                    code,
                    null);
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            error = new NyxIdMultipartUploadProviderError(
                "invalid_provider_response",
                "workflow_file_submit provider response was not valid JSON.",
                null,
                null);
            return true;
        }
    }

    private static WorkflowFileMultipartUploadResult ReadProviderSuccess(
        string? response,
        string selector)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return WorkflowFileMultipartUploadResult.Failure(
                "invalid_provider_response",
                "workflow_file_submit provider response was empty.");
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return WorkflowFileMultipartUploadResult.Failure(
                    "invalid_provider_response",
                    "workflow_file_submit provider response root was not a JSON object.");
            }

            var providerCode = TryReadInt(root, "code");
            if (providerCode is { } code && code != 0)
            {
                return WorkflowFileMultipartUploadResult.Failure(
                    "provider_error",
                    $"provider_code={code.ToString(CultureInfo.InvariantCulture)}",
                    code);
            }

            if (root.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.True)
            {
                return WorkflowFileMultipartUploadResult.Failure(
                    "provider_error",
                    "workflow_file_submit provider returned an error envelope.",
                    providerCode,
                    TryReadInt(root, "status"));
            }

            var outputCode = ReadStringBySelector(root, selector);
            if (outputCode == null)
            {
                return WorkflowFileMultipartUploadResult.Failure(
                    "missing_output_code",
                    "workflow_file_submit response did not include the required output_code.",
                    providerCode);
            }

            return IsSafeOutputCode(outputCode, selector)
                ? WorkflowFileMultipartUploadResult.Success(outputCode, providerCode)
                : WorkflowFileMultipartUploadResult.Failure(
                    "invalid_provider_response",
                    "workflow_file_submit response selected output_code was not a safe resource identifier.",
                    providerCode);
        }
        catch (JsonException)
        {
            return WorkflowFileMultipartUploadResult.Failure(
                "invalid_provider_response",
                "workflow_file_submit provider response was not valid JSON.");
        }
    }

    private static bool IsSafeOutputCode(string outputCode, string selector)
    {
        if (outputCode.Length > MaxOutputCodeLength ||
            outputCode.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsKnownOpaqueResourceSelector(selector) && IsBase62ResourceIdentifier(outputCode))
            return true;

        if (!IsLikelyResourceIdentifier(outputCode) &&
            TryDecodeBase64Text(outputCode, out _))
        {
            return false;
        }

        return true;
    }

    private static bool IsKnownOpaqueResourceSelector(string selector) =>
        string.Equals(selector, "data.file_token", StringComparison.Ordinal) ||
        string.Equals(selector, "file_token", StringComparison.Ordinal);

    private static bool IsBase62ResourceIdentifier(string value)
    {
        if (value.Length is < 16 or > 128)
            return false;

        var hasDigit = false;
        var hasLetter = false;
        foreach (var ch in value)
        {
            if (ch is >= '0' and <= '9')
            {
                hasDigit = true;
                continue;
            }

            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                hasLetter = true;
                continue;
            }

            return false;
        }

        return hasDigit && hasLetter;
    }

    private static bool IsLikelyResourceIdentifier(string value)
    {
        var separatorIndex = value.IndexOf('_', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
            return false;

        if (value[0] is not (>= 'a' and <= 'z'))
            return false;

        var hasSuffixDigit = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch is >= '0' and <= '9')
            {
                if (i > separatorIndex)
                    hasSuffixDigit = true;
                continue;
            }

            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or '-')
                continue;

            return false;
        }

        return hasSuffixDigit;
    }

    private static bool TryDecodeBase64Text(string value, out byte[] decoded)
    {
        decoded = [];
        if (value.Length < 3)
            return false;

        var paddingStarted = false;
        var hasUrlSafeCharacter = false;
        var hasStandardOnlyCharacter = false;
        foreach (var ch in value)
        {
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (paddingStarted)
                    return false;

                continue;
            }

            if (ch is '-' or '_')
            {
                if (paddingStarted)
                    return false;

                hasUrlSafeCharacter = true;
                continue;
            }

            if (ch is '+' or '/')
            {
                if (paddingStarted)
                    return false;

                hasStandardOnlyCharacter = true;
                continue;
            }

            if (ch == '=')
            {
                paddingStarted = true;
                continue;
            }

            return false;
        }

        if (hasUrlSafeCharacter && hasStandardOnlyCharacter)
            return false;

        var unpadded = value.TrimEnd('=');
        var paddingLength = value.Length - unpadded.Length;
        if (unpadded.Length == 0 ||
            paddingLength > 2 ||
            unpadded.Length % 4 == 1)
        {
            return false;
        }

        var requiredPadding = (4 - unpadded.Length % 4) % 4;
        var normalized = hasUrlSafeCharacter
            ? unpadded.Replace('-', '+').Replace('_', '/')
            : unpadded;
        normalized = requiredPadding == 0
            ? normalized
            : normalized.PadRight(normalized.Length + requiredPadding, '=');

        Span<byte> buffer = stackalloc byte[normalized.Length];
        if (!Convert.TryFromBase64String(normalized, buffer, out var bytesWritten))
            return false;

        decoded = buffer[..bytesWritten].ToArray();
        return true;
    }

    private static string? ReadStringBySelector(JsonElement root, string selector)
    {
        var current = root;
        foreach (var segment in selector.Split('.', StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String
            ? Normalize(current.GetString())
            : current.ValueKind == JsonValueKind.Number
                ? current.GetRawText()
                : null;
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

    private sealed record NyxIdMultipartUploadProviderError(
        string Error,
        string Detail,
        int? ProviderCode,
        int? HttpStatus);
}
