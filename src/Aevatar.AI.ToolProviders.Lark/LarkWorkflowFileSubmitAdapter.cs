using System.Globalization;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkWorkflowFileSubmitAdapter(ILarkNyxClient client)
    : IWorkflowConnectedServiceFileSubmitAdapter
{
    public const string ProviderName = "lark";
    private const string DriveMediaTarget = "lark_drive_media";
    private const string ApprovalFileTarget = "lark_approval_file";
    private const long MaxFileBytes = 20L * 1024L * 1024L;
    private const long MaxApprovalImageBytes = 10L * 1024L * 1024L;
    private const long MaxApprovalAttachmentBytes = 30L * 1024L * 1024L;

    private static readonly HashSet<string> AllowedParentTypes = new(StringComparer.Ordinal)
    {
        "doc_image",
        "doc_file",
        "sheet_image",
        "sheet_file",
        "bitable_image",
        "bitable_file",
        "docx_image",
        "docx_file",
        "ccm_import_open",
    };

    private static readonly HashSet<string> AllowedApprovalFileTypes = new(StringComparer.Ordinal)
    {
        "image",
        "attachment",
    };

    private static readonly HashSet<string> AllowedApprovalImageMediaTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg",
        "image/png",
    };

    private static readonly HashSet<string> AllowedApprovalAttachmentMediaTypes = new(StringComparer.Ordinal)
    {
        "application/msword",
        "application/octet-stream",
        "application/pdf",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "image/jpeg",
        "image/png",
        "text/csv",
        "text/plain",
    };

    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.Ordinal)
    {
        "application/json",
        "application/msword",
        "application/octet-stream",
        "application/pdf",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "image/gif",
        "image/jpeg",
        "image/png",
        "text/csv",
        "text/markdown",
        "text/plain",
    };

    private static readonly WorkflowConnectedServiceFileSubmitTarget DriveMediaSubmitTarget = new(
        Target: DriveMediaTarget,
        Provider: ProviderName,
        OutputField: "file_token",
        MaxFileBytes: MaxFileBytes,
        AllowedMediaTypes: AllowedMediaTypes,
        Arguments: new Dictionary<string, WorkflowConnectedServiceFileSubmitArgumentPolicy>(StringComparer.Ordinal)
        {
            ["parent_type"] = new(
                "parent_type",
                Required: true,
                AllowedValues: AllowedParentTypes,
                UnsupportedValueError: "unsupported_parent_type"),
            ["parent_node"] = new("parent_node", Required: true, MissingError: "missing_parent_node"),
            ["checksum"] = new("checksum"),
            ["extra"] = new("extra"),
        });

    private static readonly WorkflowConnectedServiceFileSubmitTarget ApprovalFileSubmitTarget = new(
        Target: ApprovalFileTarget,
        Provider: ProviderName,
        OutputField: "file_code",
        MaxFileBytes: MaxApprovalAttachmentBytes,
        AllowedMediaTypes: AllowedApprovalAttachmentMediaTypes,
        Arguments: new Dictionary<string, WorkflowConnectedServiceFileSubmitArgumentPolicy>(StringComparer.Ordinal)
        {
            ["file_type"] = new(
                "file_type",
                Required: true,
                AllowedValues: AllowedApprovalFileTypes,
                MissingError: "unsupported_file_type",
                UnsupportedValueError: "unsupported_file_type"),
        },
        MaxFileBytesByArgumentValue: new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal)
        {
            ["file_type"] = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["image"] = MaxApprovalImageBytes,
                ["attachment"] = MaxApprovalAttachmentBytes,
            },
        },
        AllowedMediaTypesByArgumentValue: new Dictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>(StringComparer.Ordinal)
        {
            ["file_type"] = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["image"] = AllowedApprovalImageMediaTypes,
                ["attachment"] = AllowedApprovalAttachmentMediaTypes,
            },
        });

    private readonly ILarkNyxClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Provider => ProviderName;

    public IReadOnlyList<WorkflowConnectedServiceFileSubmitTarget> Targets { get; } =
    [
        DriveMediaSubmitTarget,
        ApprovalFileSubmitTarget,
    ];

    public async ValueTask<WorkflowConnectedServiceFileSubmitResult> SubmitAsync(
        WorkflowConnectedServiceFileSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string response;
        try
        {
            response = request.Target.Target == ApprovalFileTarget
                ? await UploadApprovalFileAsync(request, cancellationToken).ConfigureAwait(false)
                : await UploadDriveMediaAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WorkflowConnectedServiceFileSubmitResult(
                Succeeded: false,
                Error: "provider_call_failed",
                Detail: "Lark file upload request failed.");
        }

        if (TryParseProviderError(response, out var providerError))
        {
            return new WorkflowConnectedServiceFileSubmitResult(
                Succeeded: false,
                Error: providerError.Error,
                Detail: providerError.Detail,
                Code: providerError.Code);
        }

        var success = ParseSuccess(response);
        return new WorkflowConnectedServiceFileSubmitResult(
            Succeeded: true,
            OutputCode: request.Target.Target == ApprovalFileTarget
                ? success.FileCode
                : success.FileToken,
            Code: success.Code);
    }

    private Task<string> UploadDriveMediaAsync(
        WorkflowConnectedServiceFileSubmitRequest request,
        CancellationToken cancellationToken)
    {
        var token = Normalize(request.CallerCredential.BearerToken)
                    ?? throw new ArgumentException("Lark file submit requires a caller bearer token.", nameof(request));
        return _client.UploadDriveMediaAsync(
            token,
            new LarkDriveMediaUploadRequest(
                request.FileName,
                request.Arguments["parent_type"],
                request.Arguments["parent_node"],
                request.SizeBytes,
                request.MediaType,
                request.Content,
                TryGetArgument(request.Arguments, "checksum"),
                TryGetArgument(request.Arguments, "extra")),
            cancellationToken);
    }

    private Task<string> UploadApprovalFileAsync(
        WorkflowConnectedServiceFileSubmitRequest request,
        CancellationToken cancellationToken)
    {
        var token = Normalize(request.CallerCredential.BearerToken)
                    ?? throw new ArgumentException("Lark file submit requires a caller bearer token.", nameof(request));
        return _client.UploadApprovalFileAsync(
            token,
            new LarkApprovalFileUploadRequest(
                request.FileName,
                request.Arguments["file_type"],
                request.SizeBytes,
                request.MediaType,
                request.Content),
            cancellationToken);
    }

    private static WorkflowFileSubmitProviderSuccess ParseSuccess(string response)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object
            ? dataProp
            : root;
        return new WorkflowFileSubmitProviderSuccess(
            TryReadString(data, "file_token") ?? TryReadString(root, "file_token"),
            TryReadString(data, "code") ?? TryReadString(data, "file_code") ?? TryReadString(root, "file_code"),
            TryReadInt(root, "code"));
    }

    private static bool TryParseProviderError(string? response, out WorkflowFileSubmitProviderError error)
    {
        error = new WorkflowFileSubmitProviderError("provider_error", "empty_lark_response", null);
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
                error = new WorkflowFileSubmitProviderError("nyx_proxy_error", detail, status);
                return true;
            }

            if (root.TryGetProperty("code", out var codeProp) &&
                codeProp.ValueKind == JsonValueKind.Number &&
                codeProp.TryGetInt32(out var code) &&
                code != 0)
            {
                var detail = $"lark_code={code.ToString(CultureInfo.InvariantCulture)}";
                error = new WorkflowFileSubmitProviderError("lark_error", detail, code);
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            error = new WorkflowFileSubmitProviderError("invalid_provider_response", "invalid_lark_response_json", null);
            return true;
        }
    }

    private static string? TryGetArgument(
        IReadOnlyDictionary<string, string> arguments,
        string name) =>
        arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string? TryReadString(JsonElement source, string name)
    {
        return source.ValueKind == JsonValueKind.Object &&
               source.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
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

    private sealed record WorkflowFileSubmitProviderSuccess(string? FileToken, string? FileCode, int? Code);

    private sealed record WorkflowFileSubmitProviderError(string Error, string Detail, int? Code);
}
