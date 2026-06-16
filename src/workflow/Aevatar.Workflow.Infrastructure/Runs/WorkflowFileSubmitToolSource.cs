using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowFileSubmitToolSource(
    IEnumerable<IWorkflowConnectedServiceFileSubmitAdapter> adapters,
    IWorkflowFileArtifactReadPort fileArtifacts,
    IOptions<WorkflowConnectedServiceFileSubmitOptions> options) : IWorkflowToolSource
{
    private readonly IReadOnlyList<IWorkflowConnectedServiceFileSubmitAdapter> _adapters =
        adapters?.ToArray() ?? throw new ArgumentNullException(nameof(adapters));
    private readonly IWorkflowFileArtifactReadPort _fileArtifacts =
        fileArtifacts ?? throw new ArgumentNullException(nameof(fileArtifacts));
    private readonly IOptions<WorkflowConnectedServiceFileSubmitOptions> _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
    {
        var targets = ResolveTargets();
        return Task.FromResult<IReadOnlyList<IWorkflowTool>>(
            targets.Count == 0
                ? []
                : [new WorkflowFileSubmitTool(_adapters, _fileArtifacts, targets)]);
    }

    private IReadOnlyList<WorkflowConnectedServiceFileSubmitTarget> ResolveTargets()
    {
        var configuredTargets = _options.Value.Targets
            .Where(static target => !string.IsNullOrWhiteSpace(target.Target) &&
                                    !string.IsNullOrWhiteSpace(target.Provider))
            .ToArray();
        var adapterTargets = _adapters
            .SelectMany(static adapter => adapter.Targets)
            .Where(static target => !string.IsNullOrWhiteSpace(target.Target) &&
                                    !string.IsNullOrWhiteSpace(target.Provider))
            .ToArray();
        return adapterTargets.Concat(configuredTargets).ToArray();
    }

    private sealed class WorkflowFileSubmitTool(
        IReadOnlyList<IWorkflowConnectedServiceFileSubmitAdapter> adapters,
        IWorkflowFileArtifactReadPort fileArtifacts,
        IReadOnlyList<WorkflowConnectedServiceFileSubmitTarget> targets) : IWorkflowTool
    {
        private const int MaxFileNameLength = 250;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IReadOnlyList<IWorkflowConnectedServiceFileSubmitAdapter> _adapters = adapters;
        private readonly IWorkflowFileArtifactReadPort _fileArtifacts = fileArtifacts;
        private readonly IReadOnlyList<WorkflowConnectedServiceFileSubmitTarget> _targets = targets;

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
            catch (WorkflowFileSubmitReservedArgumentException ex)
            {
                return Error("reserved_argument", ex.Message);
            }
            catch (JsonException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }

            var target = ResolveTarget(arguments.Target);
            if (target == null)
                return Error(arguments.Target, "invalid_target", "workflow_file_submit only supports registered file submit targets.");

            var token = Normalize(request.CallerCredential.BearerToken);
            if (token == null)
                return Error(target.Target, "missing_bearer", "workflow_file_submit requires a workflow caller bearer token.");

            var adapter = ResolveAdapter(target);
            if (adapter == null)
                return Error(target.Target, "invalid_target", "workflow_file_submit target provider is not registered.");

            var policyValidation = ValidateTargetPolicy(target, arguments);
            if (policyValidation != null)
                return policyValidation;

            var argumentValidation = ValidateRequestedFileRef(target, arguments, request, out var argumentFileName);
            if (argumentValidation != null)
                return argumentValidation;

            WorkflowFileRef descriptor;
            try
            {
                descriptor = await _fileArtifacts.DescribeAsync(arguments.FileRef, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSafeArtifactFailure(ex))
            {
                return Error(target.Target, "artifact_unavailable", SafeArtifactDetail(ex));
            }

            var descriptorValidation = ValidateDescriptor(
                target,
                descriptor,
                arguments,
                request,
                argumentFileName,
                out var uploadFileName,
                out var mediaType);
            if (descriptorValidation != null)
                return descriptorValidation;

            WorkflowFileArtifactContent artifact;
            try
            {
                artifact = await _fileArtifacts.OpenReadAsync(arguments.FileRef, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSafeArtifactFailure(ex))
            {
                return Error(target.Target, "artifact_unavailable", SafeArtifactDetail(ex));
            }

            await using (artifact.Content.ConfigureAwait(false))
            {
                if (!DescriptorMatchesValidatedDescriptor(artifact.FileRef, descriptor))
                    return Error(target.Target, "invalid_file_scope", "workflow_file_submit artifact owner does not match the current workflow context.");

                WorkflowConnectedServiceFileSubmitResult submitResult;
                try
                {
                    submitResult = await adapter.SubmitAsync(
                        new WorkflowConnectedServiceFileSubmitRequest(
                            target,
                            descriptor,
                            uploadFileName,
                            mediaType,
                            descriptor.SizeBytes,
                            artifact.Content,
                            new WorkflowCallerCredential(token),
                            arguments.SubmitArguments),
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Error(target.Target, "provider_call_failed", "Connected-service file submit failed.");
                }

                if (!submitResult.Succeeded)
                    return Error(
                        target.Target,
                        Normalize(submitResult.Error) ?? "provider_call_failed",
                        Normalize(submitResult.Detail) ?? "Connected-service file submit failed.",
                        submitResult.Code);

                var outputCode = Normalize(submitResult.OutputCode);
                if (outputCode == null)
                    return Error(target.Target, ResolveMissingOutputError(target.OutputField), "Connected-service file submit response did not include the required file code.");

                return WorkflowToolExecutionResult.Success(JsonSerializer.Serialize(
                    new WorkflowFileSubmitResult(
                        Success: true,
                        Provider: target.Provider,
                        Target: target.Target,
                        FileToken: IsOutputField(target, "file_token") ? outputCode : null,
                        FileCode: IsOutputField(target, "file_code") ? outputCode : null,
                        OutputCode: IsTokenOrCodeOutputField(target) ? null : outputCode,
                        OutputField: Normalize(target.OutputField) ?? "output_code",
                        FileName: uploadFileName,
                        SizeBytes: descriptor.SizeBytes,
                        Code: submitResult.Code),
                    JsonOptions));
            }
        }

        private WorkflowConnectedServiceFileSubmitTarget? ResolveTarget(string targetName)
        {
            var matches = _targets
                .Where(target => string.Equals(target.Target, targetName, StringComparison.Ordinal))
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private IWorkflowConnectedServiceFileSubmitAdapter? ResolveAdapter(
            WorkflowConnectedServiceFileSubmitTarget target)
        {
            foreach (var adapter in _adapters)
            {
                if (string.Equals(adapter.Provider, target.Provider, StringComparison.Ordinal))
                    return adapter;
            }

            return null;
        }

        private static WorkflowToolExecutionResult? ValidateTargetPolicy(
            WorkflowConnectedServiceFileSubmitTarget target,
            WorkflowFileSubmitArguments arguments)
        {
            foreach (var policy in target.Arguments.Values)
            {
                var policyName = NormalizeArgumentName(policy.Name);
                var hasValue = arguments.SubmitArguments.TryGetValue(policyName, out var argumentValue) &&
                               !string.IsNullOrWhiteSpace(argumentValue);
                if (policy.Required && !hasValue)
                    return Error(
                        target.Target,
                        Normalize(policy.MissingError) ?? "missing_argument",
                        $"workflow_file_submit requires {policyName}.");

                if (hasValue &&
                    policy.AllowedValues is { Count: > 0 } &&
                    !policy.AllowedValues.Contains(argumentValue!))
                {
                    return Error(
                        target.Target,
                        Normalize(policy.UnsupportedValueError) ?? "unsupported_argument_value",
                        $"workflow_file_submit does not support the requested {policyName}.");
                }
            }

            foreach (var argumentName in arguments.SubmitArguments.Keys)
            {
                if (!target.Arguments.ContainsKey(argumentName))
                    return Error(target.Target, "unsupported_argument", $"workflow_file_submit does not support {argumentName} for the requested target.");
            }

            return null;
        }

        private static WorkflowToolExecutionResult? ValidateRequestedFileRef(
            WorkflowConnectedServiceFileSubmitTarget target,
            WorkflowFileSubmitArguments arguments,
            WorkflowToolExecutionRequest request,
            out string? argumentFileName)
        {
            argumentFileName = ResolveFileName(arguments.FileName, arguments.FileRef.FileName);
            if (!HasStableFileRef(arguments.FileRef))
                return Error(target.Target, "invalid_file_ref", "workflow_file_submit file_ref requires file_id or artifact_id.");

            var requestedRunId = Normalize(arguments.FileRef.OwnerRunId);
            var requestedScopeId = Normalize(arguments.FileRef.OwnerScopeId);
            if (requestedRunId == null || !MatchesAnyRunContext(requestedRunId, request))
                return Error(target.Target, "invalid_file_scope", "workflow_file_submit requires a file_ref owned by the current workflow run.");
            if (requestedScopeId != null && !MatchesScopeContext(requestedScopeId, request))
                return Error(target.Target, "invalid_file_scope", "workflow_file_submit file_ref owner_scope_id does not match the current workflow scope.");

            if (arguments.FileRef.SizeBytes <= 0)
                return Error(target.Target, "invalid_file_size", "workflow_file_submit file_ref size_bytes must be greater than zero.");
            if (arguments.FileRef.SizeBytes > ResolveMaxFileBytes(target, arguments.SubmitArguments))
                return Error(target.Target, "file_too_large", "workflow_file_submit file_ref exceeds the target size limit.");

            return argumentFileName is { Length: > MaxFileNameLength }
                ? Error(target.Target, "invalid_file_name", "workflow_file_submit file_name must be 250 characters or fewer.")
                : null;
        }

        private static WorkflowToolExecutionResult? ValidateDescriptor(
            WorkflowConnectedServiceFileSubmitTarget target,
            WorkflowFileRef descriptor,
            WorkflowFileSubmitArguments arguments,
            WorkflowToolExecutionRequest request,
            string? argumentFileName,
            out string uploadFileName,
            out string mediaType)
        {
            uploadFileName = string.Empty;
            mediaType = string.Empty;

            if (descriptor.SizeBytes <= 0)
                return Error(target.Target, "invalid_file_size", "workflow_file_submit artifact size_bytes must be greater than zero.");
            if (descriptor.SizeBytes > ResolveMaxFileBytes(target, arguments.SubmitArguments))
                return Error(target.Target, "file_too_large", "workflow_file_submit artifact exceeds the target size limit.");
            if (arguments.FileRef.SizeBytes > 0 && descriptor.SizeBytes != arguments.FileRef.SizeBytes)
                return Error(target.Target, "artifact_size_mismatch", "workflow_file_submit artifact size does not match file_ref size_bytes.");
            if (!DescriptorOwnerMatches(descriptor, request))
                return Error(target.Target, "invalid_file_scope", "workflow_file_submit artifact owner does not match the current workflow context.");

            var resolvedFileName = ResolveFileName(arguments.FileName, descriptor.FileName) ?? argumentFileName;
            if (resolvedFileName == null)
                return Error(target.Target, "missing_file_name", "workflow_file_submit requires file_name from arguments or artifact descriptor.");
            if (resolvedFileName.Length > MaxFileNameLength)
                return Error(target.Target, "invalid_file_name", "workflow_file_submit file_name must be 250 characters or fewer.");

            var resolvedMediaType = NormalizeMediaType(descriptor.MediaType) ??
                                    NormalizeMediaType(arguments.FileRef.MediaType);
            if (resolvedMediaType == null)
                return Error(target.Target, "unsupported_media_type", "workflow_file_submit requires a workflow file media type.");
            if (!ResolveAllowedMediaTypes(target, arguments.SubmitArguments).Contains(resolvedMediaType))
                return Error(target.Target, "unsupported_media_type", "workflow_file_submit does not support the requested media type.");

            uploadFileName = resolvedFileName;
            mediaType = resolvedMediaType;
            return null;
        }

        private static WorkflowFileSubmitArguments ParseArguments(string? argumentsJson)
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

            var target = OptionalString(root, "target", "target") ?? string.Empty;
            var fileName = OptionalString(root, "fileName", "file_name");
            var submitArguments = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                var propertyName = NormalizeArgumentName(property.Name);
                if (StandardArgumentNames.Contains(propertyName))
                    continue;
                if (ReservedArgumentNames.Contains(propertyName))
                    throw new WorkflowFileSubmitReservedArgumentException(
                        $"workflow_file_submit {propertyName} is controlled by host configuration.");
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException($"workflow_file_submit {propertyName} must be a string.");

                var value = Normalize(property.Value.GetString());
                if (value != null)
                    submitArguments[propertyName] = value;
            }

            return new WorkflowFileSubmitArguments(
                ParseFileRef(fileRefElement),
                target,
                fileName,
                submitArguments);
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

        private static string NormalizeArgumentName(string value)
        {
            if (value.Length == 0)
                return string.Empty;
            var builder = new StringBuilder(value.Length + 4);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '-')
                {
                    builder.Append('_');
                    continue;
                }

                if (char.IsUpper(c))
                {
                    if (i > 0 && builder.Length > 0 && builder[^1] != '_')
                        builder.Append('_');
                    builder.Append(char.ToLowerInvariant(c));
                    continue;
                }

                builder.Append(char.ToLowerInvariant(c));
            }

            return builder.ToString();
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

        private static long ResolveMaxFileBytes(
            WorkflowConnectedServiceFileSubmitTarget target,
            IReadOnlyDictionary<string, string> arguments)
        {
            var maxFileBytes = target.MaxFileBytes;
            if (target.MaxFileBytesByArgumentValue == null)
                return maxFileBytes;

            foreach (var (argumentName, limitsByValue) in target.MaxFileBytesByArgumentValue)
            {
                var normalizedArgumentName = NormalizeArgumentName(argumentName);
                if (!arguments.TryGetValue(normalizedArgumentName, out var argumentValue))
                    continue;
                if (limitsByValue.TryGetValue(argumentValue, out var scopedLimit))
                    maxFileBytes = Math.Min(maxFileBytes, scopedLimit);
            }

            return maxFileBytes;
        }

        private static IReadOnlySet<string> ResolveAllowedMediaTypes(
            WorkflowConnectedServiceFileSubmitTarget target,
            IReadOnlyDictionary<string, string> arguments)
        {
            if (target.AllowedMediaTypesByArgumentValue == null)
                return target.AllowedMediaTypes;

            foreach (var (argumentName, mediaTypesByValue) in target.AllowedMediaTypesByArgumentValue)
            {
                var normalizedArgumentName = NormalizeArgumentName(argumentName);
                if (!arguments.TryGetValue(normalizedArgumentName, out var argumentValue))
                    continue;
                if (mediaTypesByValue.TryGetValue(argumentValue, out var scopedMediaTypes))
                    return scopedMediaTypes;
            }

            return target.AllowedMediaTypes;
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
            WorkflowFileRef validated) =>
            string.Equals(Normalize(opened.FileId), Normalize(validated.FileId), StringComparison.Ordinal) &&
            string.Equals(Normalize(opened.ArtifactId), Normalize(validated.ArtifactId), StringComparison.Ordinal) &&
            opened.SizeBytes == validated.SizeBytes &&
            string.Equals(Normalize(opened.Sha256), Normalize(validated.Sha256), StringComparison.Ordinal) &&
            string.Equals(Normalize(opened.OwnerRunId), Normalize(validated.OwnerRunId), StringComparison.Ordinal) &&
            string.Equals(Normalize(opened.OwnerScopeId), Normalize(validated.OwnerScopeId), StringComparison.Ordinal);

        private static string? ResolveFileName(string? argumentFileName, string? descriptorFileName) =>
            Normalize(argumentFileName) ?? Normalize(descriptorFileName);

        private static bool IsOutputField(WorkflowConnectedServiceFileSubmitTarget target, string outputField) =>
            string.Equals(Normalize(target.OutputField), outputField, StringComparison.Ordinal);

        private static bool IsTokenOrCodeOutputField(WorkflowConnectedServiceFileSubmitTarget target) =>
            IsOutputField(target, "file_token") || IsOutputField(target, "file_code");

        private static string ResolveMissingOutputError(string outputField) =>
            string.Equals(Normalize(outputField), "file_token", StringComparison.Ordinal)
                ? "missing_file_token"
                : string.Equals(Normalize(outputField), "file_code", StringComparison.Ordinal)
                    ? "missing_file_code"
                    : "missing_output_code";

        private static WorkflowToolExecutionResult Error(
            string? target,
            string error,
            string detail,
            int? code = null) =>
            WorkflowToolExecutionResult.Success(JsonSerializer.Serialize(
                new WorkflowFileSubmitError(
                    Success: false,
                    Target: string.IsNullOrWhiteSpace(target) ? null : target,
                    Error: error,
                    Detail: detail,
                    Code: code),
                JsonOptions));

        private static WorkflowToolExecutionResult Error(string error, string detail) =>
            Error(target: null, error, detail);

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

        private static readonly HashSet<string> StandardArgumentNames = new(StringComparer.Ordinal)
        {
            "target",
            "file_ref",
            "file_name",
        };

        private static readonly HashSet<string> ReservedArgumentNames = new(StringComparer.Ordinal)
        {
            "service_slug",
            "path",
            "method",
            "file_field_name",
            "headers",
            "body",
        };
    }

    private sealed record WorkflowFileSubmitArguments(
        WorkflowFileRef FileRef,
        string Target,
        string? FileName,
        IReadOnlyDictionary<string, string> SubmitArguments);

    private sealed record WorkflowFileSubmitResult(
        bool Success,
        string Provider,
        string Target,
        string? FileToken,
        string? FileCode,
        string? OutputCode,
        string OutputField,
        string FileName,
        long SizeBytes,
        int? Code);

    private sealed record WorkflowFileSubmitError(
        bool Success,
        string? Target,
        string Error,
        string Detail,
        int? Code);

    private sealed class WorkflowFileSubmitReservedArgumentException(string message) : ArgumentException(message);
}
