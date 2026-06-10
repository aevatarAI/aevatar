using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkWorkflowFileSubmitToolSource : IWorkflowToolSource
{
    private readonly LarkToolOptions _options;
    private readonly ILarkNyxClient _client;
    private readonly IWorkflowFileArtifactReadPort? _fileArtifacts;

    public LarkWorkflowFileSubmitToolSource(
        LarkToolOptions options,
        ILarkNyxClient client,
        IServiceProvider services)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(services);
        _fileArtifacts = services.GetService<IWorkflowFileArtifactReadPort>();
    }

    public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IWorkflowTool>>(
            !_options.EnableWorkflowFileSubmit ||
            string.IsNullOrWhiteSpace(_options.ProviderSlug) ||
            _fileArtifacts == null
            ? []
            : [new WorkflowFileSubmitTool(_fileArtifacts, _client)]);

    private sealed class WorkflowFileSubmitTool(
        IWorkflowFileArtifactReadPort fileArtifacts,
        ILarkNyxClient client) : IWorkflowTool
    {
        private const string Target = "lark_drive_media";
        private const long MaxFileBytes = 20L * 1024L * 1024L;
        private const int MaxFileNameLength = 250;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IWorkflowFileArtifactReadPort _fileArtifacts = fileArtifacts;
        private readonly ILarkNyxClient _client = client;

        public string Name => "workflow_file_submit";

        public async Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            WorkflowFileSubmitArguments arguments;
            try
            {
                arguments = ParseArguments(request.ArgumentsJson);
            }
            catch (JsonException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }

            var token = Normalize(request.CallerCredential.BearerToken);
            if (token == null)
                return Error("missing_bearer", "workflow_file_submit requires a workflow caller bearer token.");

            var argumentValidation = ValidateArguments(arguments, request, out var argumentFileName);
            if (argumentValidation != null)
                return argumentValidation;

            WorkflowFileRef descriptor;
            try
            {
                descriptor = await _fileArtifacts.DescribeAsync(arguments.FileRef, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSafeArtifactFailure(ex))
            {
                return Error("artifact_unavailable", SafeArtifactDetail(ex));
            }

            var descriptorValidation = ValidateDescriptor(
                descriptor,
                arguments,
                request,
                argumentFileName,
                out var uploadFileName,
                out var contentType);
            if (descriptorValidation != null)
                return descriptorValidation;

            WorkflowFileArtifactContent artifact;
            try
            {
                artifact = await _fileArtifacts.OpenReadAsync(arguments.FileRef, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSafeArtifactFailure(ex))
            {
                return Error("artifact_unavailable", SafeArtifactDetail(ex));
            }

            await using (artifact.Content.ConfigureAwait(false))
            {
                if (!DescriptorMatchesValidatedDescriptor(artifact.FileRef, descriptor))
                    return Error("invalid_file_scope", "workflow_file_submit artifact owner does not match the current workflow context.");

                string response;
                try
                {
                    response = await _client.UploadDriveMediaAsync(
                        token,
                        new LarkDriveMediaUploadRequest(
                            uploadFileName,
                            arguments.ParentType,
                            arguments.ParentNode,
                            descriptor.SizeBytes,
                            contentType,
                            artifact.Content,
                            arguments.Checksum,
                            arguments.Extra),
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Error("provider_call_failed", "Lark media upload request failed.");
                }

                if (TryParseProviderError(response, out var providerError))
                    return Error(providerError.Error, providerError.Detail, providerError.Code);

                var success = ParseSuccess(response);
                if (string.IsNullOrWhiteSpace(success.FileToken))
                    return Error("missing_file_token", "Lark media upload response did not include file_token.");

                return WorkflowToolExecutionResult.Success(JsonSerializer.Serialize(
                    new WorkflowFileSubmitResult(
                        Success: true,
                        Provider: "lark",
                        Target: Target,
                        FileToken: success.FileToken,
                        ParentType: arguments.ParentType,
                        ParentNode: arguments.ParentNode,
                        FileName: uploadFileName,
                        SizeBytes: descriptor.SizeBytes,
                        Code: success.Code),
                    JsonOptions));
            }
        }

        private static WorkflowToolExecutionResult? ValidateArguments(
            WorkflowFileSubmitArguments arguments,
            WorkflowToolExecutionRequest request,
            out string? argumentFileName)
        {
            argumentFileName = null;
            if (!string.Equals(arguments.Target, Target, StringComparison.Ordinal))
                return Error("invalid_target", "workflow_file_submit only supports target lark_drive_media.");

            if (!AllowedParentTypes.Contains(arguments.ParentType))
                return Error("unsupported_parent_type", "workflow_file_submit does not support the requested parent_type.");

            if (string.IsNullOrWhiteSpace(arguments.ParentNode))
                return Error("missing_parent_node", "workflow_file_submit requires parent_node.");

            if (!HasStableFileRef(arguments.FileRef))
                return Error("invalid_file_ref", "workflow_file_submit file_ref requires file_id or artifact_id.");

            var requestedRunId = Normalize(arguments.FileRef.OwnerRunId);
            var requestedScopeId = Normalize(arguments.FileRef.OwnerScopeId);
            if (requestedRunId == null || !MatchesAnyRunContext(requestedRunId, request))
                return Error("invalid_file_scope", "workflow_file_submit requires a file_ref owned by the current workflow run.");
            if (requestedScopeId != null && !MatchesScopeContext(requestedScopeId, request))
                return Error("invalid_file_scope", "workflow_file_submit file_ref owner_scope_id does not match the current workflow scope.");

            if (arguments.FileRef.SizeBytes <= 0)
                return Error("invalid_file_size", "workflow_file_submit file_ref size_bytes must be greater than zero.");
            if (arguments.FileRef.SizeBytes > MaxFileBytes)
                return Error("file_too_large", "workflow_file_submit supports files up to 20971520 bytes.");

            argumentFileName = ResolveFileName(arguments.FileName, arguments.FileRef.FileName);
            return argumentFileName is { Length: > MaxFileNameLength }
                ? Error("invalid_file_name", "workflow_file_submit file_name must be 250 characters or fewer.")
                : null;
        }

        private static WorkflowToolExecutionResult? ValidateDescriptor(
            WorkflowFileRef descriptor,
            WorkflowFileSubmitArguments arguments,
            WorkflowToolExecutionRequest request,
            string? argumentFileName,
            out string uploadFileName,
            out string contentType)
        {
            uploadFileName = string.Empty;
            contentType = string.Empty;

            if (descriptor.SizeBytes <= 0)
                return Error("invalid_file_size", "workflow_file_submit artifact size_bytes must be greater than zero.");
            if (descriptor.SizeBytes > MaxFileBytes)
                return Error("file_too_large", "workflow_file_submit supports files up to 20971520 bytes.");
            if (arguments.FileRef.SizeBytes > 0 && descriptor.SizeBytes != arguments.FileRef.SizeBytes)
                return Error("artifact_size_mismatch", "workflow_file_submit artifact size does not match file_ref size_bytes.");
            if (!DescriptorOwnerMatches(descriptor, request))
                return Error("invalid_file_scope", "workflow_file_submit artifact owner does not match the current workflow context.");

            var resolvedFileName = ResolveFileName(arguments.FileName, descriptor.FileName) ?? argumentFileName;
            if (resolvedFileName == null)
                return Error("missing_file_name", "workflow_file_submit requires file_name from arguments or artifact descriptor.");
            if (resolvedFileName.Length > MaxFileNameLength)
                return Error("invalid_file_name", "workflow_file_submit file_name must be 250 characters or fewer.");

            var resolvedContentType = NormalizeMediaType(descriptor.MediaType) ??
                                      NormalizeMediaType(arguments.FileRef.MediaType);
            if (resolvedContentType == null)
                return Error("unsupported_media_type", "workflow_file_submit requires a workflow file media type.");
            if (!AllowedMediaTypes.Contains(resolvedContentType))
                return Error("unsupported_media_type", "workflow_file_submit does not support the requested media type.");

            uploadFileName = resolvedFileName;
            contentType = resolvedContentType;
            return null;
        }

        private static WorkflowFileSubmitArguments ParseArguments(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                throw new ArgumentException("workflow_file_submit arguments are required.");

            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("workflow_file_submit arguments must be a JSON object.");

            if (!TryGetProperty(root, "fileRef", "file_ref", out var fileRefElement) ||
                fileRefElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("workflow_file_submit requires a file_ref object.");

            var fileRef = ParseFileRef(fileRefElement);
            return new WorkflowFileSubmitArguments(
                fileRef,
                OptionalString(root, "target", "target") ?? string.Empty,
                OptionalString(root, "parentType", "parent_type") ?? string.Empty,
                OptionalString(root, "parentNode", "parent_node") ?? string.Empty,
                OptionalString(root, "fileName", "file_name"),
                OptionalString(root, "checksum", "checksum"),
                OptionalString(root, "extra", "extra"));
        }

        private static WorkflowFileRef ParseFileRef(JsonElement fileRefElement)
        {
            var sourceKind = WorkflowFileSourceKind.Unspecified;
            if (TryGetProperty(fileRefElement, "sourceKind", "source_kind", out var sourceKindElement))
                sourceKind = ParseSourceKind(sourceKindElement);

            return new WorkflowFileRef
            {
                FileId = OptionalString(fileRefElement, "fileId", "file_id"),
                ArtifactId = OptionalString(fileRefElement, "artifactId", "artifact_id"),
                SourceKind = sourceKind,
                SourceMessageId = OptionalString(fileRefElement, "sourceMessageId", "source_message_id"),
                SourceResourceKey = OptionalString(fileRefElement, "sourceResourceKey", "source_resource_key"),
                FileName = OptionalString(fileRefElement, "fileName", "file_name"),
                MediaType = OptionalString(fileRefElement, "mediaType", "media_type"),
                SizeBytes = OptionalInt64(fileRefElement, "sizeBytes", "size_bytes") ?? 0,
                Sha256 = OptionalString(fileRefElement, "sha256", "sha256"),
                CreatedAtUnixMs = OptionalInt64(fileRefElement, "createdAtUnixMs", "created_at_unix_ms") ?? 0,
                ExpiresAtUnixMs = OptionalInt64(fileRefElement, "expiresAtUnixMs", "expires_at_unix_ms") ?? 0,
                OwnerRunId = OptionalString(fileRefElement, "ownerRunId", "owner_run_id"),
                OwnerScopeId = OptionalString(fileRefElement, "ownerScopeId", "owner_scope_id"),
            };
        }

        private static WorkflowFileSourceKind ParseSourceKind(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric))
                return Enum.IsDefined(typeof(WorkflowFileSourceKind), numeric)
                    ? (WorkflowFileSourceKind)numeric
                    : WorkflowFileSourceKind.Unspecified;

            if (element.ValueKind != JsonValueKind.String)
                return WorkflowFileSourceKind.Unspecified;

            return NormalizeKey(element.GetString()) switch
            {
                "chatinput" => WorkflowFileSourceKind.ChatInput,
                "formupload" => WorkflowFileSourceKind.FormUpload,
                "connectedserviceresource" => WorkflowFileSourceKind.ConnectedServiceResource,
                "externalresource" => WorkflowFileSourceKind.ExternalResource,
                "generated" => WorkflowFileSourceKind.Generated,
                _ => WorkflowFileSourceKind.Unspecified,
            };
        }

        private static bool TryGetProperty(
            JsonElement source,
            string camelCaseName,
            string snakeCaseName,
            out JsonElement value)
        {
            if (source.TryGetProperty(camelCaseName, out value))
                return true;
            return source.TryGetProperty(snakeCaseName, out value);
        }

        private static string? OptionalString(JsonElement source, string camelCaseName, string snakeCaseName)
        {
            if (!TryGetProperty(source, camelCaseName, snakeCaseName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            return value.ValueKind == JsonValueKind.String
                ? Normalize(value.GetString())
                : throw new ArgumentException($"workflow_file_submit {snakeCaseName} must be a string.");
        }

        private static long? OptionalInt64(JsonElement source, string camelCaseName, string snakeCaseName)
        {
            if (!TryGetProperty(source, camelCaseName, snakeCaseName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
                ? parsed
                : throw new ArgumentException($"workflow_file_submit {snakeCaseName} must be an integer.");
        }

        private static string? Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string NormalizeKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c is '_' or '-' or ' ')
                    continue;
                builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
        }

        private static bool HasStableFileRef(WorkflowFileRef fileRef) =>
            !string.IsNullOrWhiteSpace(fileRef.FileId) ||
            !string.IsNullOrWhiteSpace(fileRef.ArtifactId);

        private static bool MatchesAnyRunContext(string ownerRunId, WorkflowToolExecutionRequest request) =>
            string.Equals(ownerRunId, Normalize(request.RunId), StringComparison.Ordinal) ||
            string.Equals(ownerRunId, Normalize(request.RuntimeContext.ParentRunId), StringComparison.Ordinal) ||
            string.Equals(ownerRunId, Normalize(request.RuntimeContext.RootRunId), StringComparison.Ordinal);

        private static bool MatchesScopeContext(string ownerScopeId, WorkflowToolExecutionRequest request) =>
            string.Equals(ownerScopeId, Normalize(request.ScopeId), StringComparison.Ordinal);

        private static bool DescriptorOwnerMatches(
            WorkflowFileRef descriptor,
            WorkflowToolExecutionRequest request)
        {
            var ownerRunId = Normalize(descriptor.OwnerRunId);
            if (ownerRunId == null || !MatchesAnyRunContext(ownerRunId, request))
                return false;

            var ownerScopeId = Normalize(descriptor.OwnerScopeId);
            return ownerScopeId == null || MatchesScopeContext(ownerScopeId, request);
        }

        private static bool DescriptorMatchesValidatedDescriptor(
            WorkflowFileRef opened,
            WorkflowFileRef validated)
        {
            return string.Equals(Normalize(opened.FileId), Normalize(validated.FileId), StringComparison.Ordinal) &&
                   string.Equals(Normalize(opened.ArtifactId), Normalize(validated.ArtifactId), StringComparison.Ordinal) &&
                   opened.SizeBytes == validated.SizeBytes &&
                   string.Equals(Normalize(opened.Sha256), Normalize(validated.Sha256), StringComparison.Ordinal) &&
                   string.Equals(Normalize(opened.OwnerRunId), Normalize(validated.OwnerRunId), StringComparison.Ordinal) &&
                   string.Equals(Normalize(opened.OwnerScopeId), Normalize(validated.OwnerScopeId), StringComparison.Ordinal);
        }

        private static string? ResolveFileName(string? argumentFileName, string? descriptorFileName) =>
            Normalize(argumentFileName) ?? Normalize(descriptorFileName);

        private static string? NormalizeMediaType(string? mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
                return null;
            var normalized = mediaType.Trim();
            var separatorIndex = normalized.IndexOf(';', StringComparison.Ordinal);
            if (separatorIndex >= 0)
                normalized = normalized[..separatorIndex].Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized.ToLowerInvariant();
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

        private static WorkflowToolExecutionResult Error(
            string error,
            string detail,
            int? code = null) =>
            WorkflowToolExecutionResult.Success(JsonSerializer.Serialize(
                new WorkflowFileSubmitError(
                    Success: false,
                    Provider: "lark",
                    Target: Target,
                    Error: error,
                    Detail: detail,
                    Code: code),
                JsonOptions));

        private static bool IsSafeArtifactFailure(Exception ex) =>
            ex is FileNotFoundException ||
            ex is InvalidOperationException ||
            ex is UnauthorizedAccessException ||
            ex is IOException;

        private static string SafeArtifactDetail(Exception ex) =>
            ex switch
            {
                FileNotFoundException => "Workflow file artifact content was not found.",
                UnauthorizedAccessException => "Workflow file artifact content could not be accessed.",
                IOException => "Workflow file artifact content could not be read.",
                _ => "Workflow file artifact content could not be read.",
            };

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
    }

    private sealed record WorkflowFileSubmitArguments(
        WorkflowFileRef FileRef,
        string Target,
        string ParentType,
        string ParentNode,
        string? FileName,
        string? Checksum,
        string? Extra);

    private sealed record WorkflowFileSubmitProviderSuccess(string? FileToken, int? Code);

    private sealed record WorkflowFileSubmitProviderError(string Error, string Detail, int? Code);

    private sealed record WorkflowFileSubmitResult(
        bool Success,
        string Provider,
        string Target,
        string FileToken,
        string ParentType,
        string ParentNode,
        string FileName,
        long SizeBytes,
        int? Code);

    private sealed record WorkflowFileSubmitError(
        bool Success,
        string Provider,
        string Target,
        string Error,
        string Detail,
        int? Code);
}
