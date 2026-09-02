using System.Text;
using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowFileSubmitToolSource(
    IFileArtifactReadPort fileArtifacts,
    IWorkflowFileMultipartUploadPolicyResolver policyResolver,
    IWorkflowFileMultipartUploadPort uploadPort) : IWorkflowToolSource
{
    private readonly IFileArtifactReadPort _fileArtifacts =
        fileArtifacts ?? throw new ArgumentNullException(nameof(fileArtifacts));
    private readonly IWorkflowFileMultipartUploadPolicyResolver _policyResolver =
        policyResolver ?? throw new ArgumentNullException(nameof(policyResolver));
    private readonly IWorkflowFileMultipartUploadPort _uploadPort =
        uploadPort ?? throw new ArgumentNullException(nameof(uploadPort));

    public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IWorkflowTool>>(
            [new WorkflowFileSubmitTool(_fileArtifacts, _policyResolver, _uploadPort)]);

    private sealed class WorkflowFileSubmitTool(
        IFileArtifactReadPort fileArtifacts,
        IWorkflowFileMultipartUploadPolicyResolver policyResolver,
        IWorkflowFileMultipartUploadPort uploadPort) : IWorkflowTool
    {
        private const int MaxFileNameLength = 250;
        private const long InternalMaxFileBytes = 30L * 1024L * 1024L;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };

        private readonly IFileArtifactReadPort _fileArtifacts = fileArtifacts;
        private readonly IWorkflowFileMultipartUploadPolicyResolver _policyResolver = policyResolver;
        private readonly IWorkflowFileMultipartUploadPort _uploadPort = uploadPort;

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
                return Error(null, "invalid_arguments", ex.Message);
            }
            catch (WorkflowFileSubmitArgumentErrorException ex)
            {
                return Error(null, ex.Error, ex.Detail);
            }
            catch (ArgumentException ex)
            {
                return Error(null, "invalid_arguments", ex.Message);
            }

            var destination = CandidateDestination.From(arguments);
            var fileRefValidation = ValidateRequestedFileRef(arguments.FileRef, request, destination);
            if (fileRefValidation != null)
                return fileRefValidation;

            var token = Normalize(request.CallerCredential.BearerToken);
            if (token == null)
                return Error(destination, "missing_bearer", "workflow_file_submit requires a workflow caller bearer token.");

            FileArtifactRef descriptor;
            try
            {
                descriptor = await _fileArtifacts.DescribeAsync(arguments.FileRef, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSafeArtifactFailure(ex))
            {
                return Error(destination, "artifact_unavailable", SafeArtifactDetail(ex));
            }

            var descriptorValidation = ValidateDescriptorBeforePolicy(descriptor, request, destination);
            if (descriptorValidation != null)
                return descriptorValidation;

            var candidate = new WorkflowFileMultipartUploadCandidate(
                arguments.FileRef,
                arguments.ServiceSlug,
                arguments.Path,
                arguments.Method,
                arguments.FileFieldName,
                arguments.FormFields,
                arguments.OutputKind,
                arguments.OutputSelector,
                arguments.MaxFileBytes);

            WorkflowFileMultipartUploadPolicyResolution resolution;
            try
            {
                resolution = await _policyResolver.ResolveAsync(
                    candidate,
                    descriptor,
                    ToUploadContext(request),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error(destination, "policy_unavailable", "workflow_file_submit multipart upload policy is unavailable.");
            }

            if (!resolution.IsAllowed || resolution.Policy == null)
            {
                return Error(
                    destination,
                    Normalize(resolution.Error) ?? "destination_not_allowed",
                    Normalize(resolution.Detail) ?? "workflow_file_submit destination is not allowed by the multipart upload policy.");
            }

            var policy = resolution.Policy;
            var policyValidation = ValidateResolvedPolicy(policy, descriptor, arguments.MaxFileBytes);
            if (policyValidation != null)
            {
                var invalidDestination = policyValidation.Value.Error == "invalid_destination"
                    ? null
                    : CandidateDestination.From(policy);
                return Error(invalidDestination, policyValidation.Value.Error, policyValidation.Value.Detail);
            }

            FileArtifactContent artifact;
            try
            {
                artifact = await _fileArtifacts.OpenReadAsync(arguments.FileRef, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSafeArtifactFailure(ex))
            {
                return Error(CandidateDestination.From(policy), "artifact_unavailable", SafeArtifactDetail(ex));
            }

            await using (artifact.Content.ConfigureAwait(false))
            {
                if (!DescriptorMatchesValidatedDescriptor(artifact.FileRef, descriptor))
                {
                    return Error(
                        CandidateDestination.From(policy),
                        "invalid_file_scope",
                        "workflow_file_submit artifact owner does not match the current workflow context.");
                }

                WorkflowFileMultipartUploadResult uploadResult;
                try
                {
                    uploadResult = await _uploadPort.UploadAsync(
                        new WorkflowFileMultipartUploadRequest(
                            new WorkflowCallerCredential(token),
                            policy.ServiceSlug,
                            policy.Path,
                            policy.Method,
                            policy.FileFieldName,
                            policy.FormFields,
                            Normalize(descriptor.FileName)!,
                            NormalizeMediaType(descriptor.MediaType)!,
                            descriptor.SizeBytes,
                            Normalize(descriptor.Sha256),
                            policy.OutputSelector,
                            artifact.Content),
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Error(
                        CandidateDestination.From(policy),
                        "provider_call_failed",
                        "workflow_file_submit multipart upload failed.");
                }

                if (!uploadResult.Succeeded)
                {
                    return Error(
                        CandidateDestination.From(policy),
                        Normalize(uploadResult.Error) ?? "provider_call_failed",
                        Normalize(uploadResult.Detail) ?? "workflow_file_submit multipart upload failed.",
                        httpStatus: uploadResult.HttpStatus,
                        providerCode: uploadResult.ProviderCode);
                }

                if (Normalize(uploadResult.OutputCode) is not { } outputCode)
                {
                    return Error(
                        CandidateDestination.From(policy),
                        "missing_output_code",
                        "workflow_file_submit response did not include the required output_code.",
                        httpStatus: uploadResult.HttpStatus,
                        providerCode: uploadResult.ProviderCode);
                }

                return WorkflowToolExecutionResult.Success(JsonSerializer.Serialize(
                    new WorkflowFileSubmitResult(
                        Success: true,
                        Error: null,
                        Detail: null,
                        OutputCode: outputCode,
                        OutputKind: policy.OutputKind,
                        HttpStatus: uploadResult.HttpStatus,
                        ProviderCode: uploadResult.ProviderCode,
                        Destination: CandidateDestination.From(policy),
                        File: new WorkflowFileSubmitFile(
                            descriptor.FileId,
                            descriptor.ArtifactId,
                            Normalize(descriptor.FileName),
                            NormalizeMediaType(descriptor.MediaType),
                            descriptor.SizeBytes,
                            Normalize(descriptor.Sha256))),
                    JsonOptions));
            }
        }

        private static WorkflowToolExecutionResult? ValidateRequestedFileRef(
            FileArtifactRef fileRef,
            WorkflowToolExecutionRequest request,
            CandidateDestination destination)
        {
            if (!HasStableFileRef(fileRef))
                return Error(destination, "invalid_file_ref", "workflow_file_submit file_ref requires file_id or artifact_id.");

            if (HasPublicFileFacts(fileRef))
            {
                return Error(
                    destination,
                    "invalid_file_ref",
                    "workflow_file_submit file_ref cannot declare file_name, media_type, size_bytes, or sha256.");
            }

            var requestedRunId = Normalize(fileRef.OwnerRunId);
            var requestedScopeId = Normalize(fileRef.OwnerScopeId);
            if (requestedRunId == null || !MatchesAnyRunContext(requestedRunId, request))
                return Error(destination, "invalid_file_scope", "workflow_file_submit requires a file_ref owned by the current workflow run.");
            if (requestedScopeId != null && !MatchesScopeContext(requestedScopeId, request))
                return Error(destination, "invalid_file_scope", "workflow_file_submit file_ref owner_scope_id does not match the current workflow scope.");

            return null;
        }

        private static WorkflowToolExecutionResult? ValidateDescriptorBeforePolicy(
            FileArtifactRef descriptor,
            WorkflowToolExecutionRequest request,
            CandidateDestination destination)
        {
            if (descriptor.SizeBytes <= 0)
                return Error(destination, "invalid_file_size", "workflow_file_submit artifact size_bytes must be greater than zero.");
            if (descriptor.SizeBytes > InternalMaxFileBytes)
                return Error(destination, "file_too_large", "workflow_file_submit artifact exceeds the runtime file size limit.");
            if (!DescriptorOwnerMatches(descriptor, request))
                return Error(destination, "invalid_file_scope", "workflow_file_submit artifact owner does not match the current workflow context.");
            if (Normalize(descriptor.FileName) == null)
                return Error(destination, "missing_file_name", "workflow_file_submit requires file_name from artifact descriptor.");
            if (Normalize(descriptor.FileName) is { Length: > MaxFileNameLength })
                return Error(destination, "invalid_file_name", "workflow_file_submit file_name must be 250 characters or fewer.");
            if (NormalizeMediaType(descriptor.MediaType) == null)
                return Error(destination, "unsupported_media_type", "workflow_file_submit requires a workflow file media type.");

            return null;
        }

        private static (string Error, string Detail)? ValidateResolvedPolicy(
            WorkflowFileMultipartUploadPolicy policy,
            FileArtifactRef descriptor,
            long? requestedMaxFileBytes)
        {
            if (string.IsNullOrWhiteSpace(policy.ServiceSlug) ||
                !IsValidRelativePath(policy.Path) ||
                string.IsNullOrWhiteSpace(policy.FileFieldName))
            {
                return ("invalid_destination", "workflow_file_submit resolved destination is invalid.");
            }

            if (!IsSupportedMethod(policy.Method))
                return ("unsupported_method", "workflow_file_submit resolved method is not supported.");

            if (policy.MaxFileBytes <= 0)
                return ("destination_not_allowed", "workflow_file_submit resolved policy did not provide a valid file size limit.");

            var maxFileBytes = Math.Min(policy.MaxFileBytes, InternalMaxFileBytes);
            if (requestedMaxFileBytes.HasValue)
            {
                if (requestedMaxFileBytes.Value <= 0)
                    return ("file_too_large", "workflow_file_submit requested max_file_bytes must be greater than zero.");

                maxFileBytes = Math.Min(maxFileBytes, requestedMaxFileBytes.Value);
            }

            if (descriptor.SizeBytes > maxFileBytes)
                return ("file_too_large", "workflow_file_submit artifact exceeds the resolved policy size limit.");

            if (string.IsNullOrWhiteSpace(policy.OutputKind) ||
                string.IsNullOrWhiteSpace(policy.OutputSelector) ||
                !IsSafeOutputSelector(policy.OutputSelector))
            {
                return ("destination_not_allowed", "workflow_file_submit resolved output policy is invalid.");
            }

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

            RejectForbiddenTopLevelArguments(root);

            if (!TryGetProperty(root, "fileRef", "file_ref", out var fileRefElement) ||
                fileRefElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("workflow_file_submit requires a file_ref object.");

            var serviceSlug = RequiredString(root, "slug", "slug");
            var path = RequiredString(root, "path", "path");
            var method = OptionalString(root, "method", "method") ?? "POST";
            var fileFieldName = OptionalString(root, "fileFieldName", "file_field_name") ?? "file";

            if (!IsValidRelativePath(path))
                throw new WorkflowFileSubmitArgumentErrorException(
                    "invalid_destination",
                    "workflow_file_submit path must be a relative downstream path.");
            if (!IsSupportedMethod(method))
                throw new WorkflowFileSubmitArgumentErrorException(
                    "unsupported_method",
                    "workflow_file_submit method must be POST, PUT, or PATCH.");
            if (string.IsNullOrWhiteSpace(serviceSlug) || string.IsNullOrWhiteSpace(fileFieldName))
                throw new WorkflowFileSubmitArgumentErrorException(
                    "invalid_destination",
                    "workflow_file_submit destination is invalid.");

            return new WorkflowFileSubmitArguments(
                ParseFileRef(fileRefElement),
                serviceSlug,
                path,
                method.Trim().ToUpperInvariant(),
                fileFieldName,
                ParseFormFields(root),
                ParseOutputKind(root),
                ParseOutputSelector(root),
                OptionalInt64(root, "maxFileBytes", "max_file_bytes"));
        }

        private static void RejectForbiddenTopLevelArguments(JsonElement root)
        {
            foreach (var property in root.EnumerateObject())
            {
                var name = NormalizeArgumentName(property.Name);
                if (ForbiddenTopLevelArgumentNames.Contains(name))
                {
                    throw new WorkflowFileSubmitArgumentErrorException(
                        "invalid_arguments",
                        $"workflow_file_submit does not accept {name}.");
                }
            }
        }

        private static FileArtifactRef ParseFileRef(JsonElement fileRefElement)
        {
            if (TryGetProperty(fileRefElement, "fileName", "file_name", out _) ||
                TryGetProperty(fileRefElement, "mediaType", "media_type", out _) ||
                TryGetProperty(fileRefElement, "sizeBytes", "size_bytes", out _) ||
                TryGetProperty(fileRefElement, "sha256", "sha256", out _))
            {
                throw new WorkflowFileSubmitArgumentErrorException(
                    "invalid_file_ref",
                    "workflow_file_submit file_ref cannot declare file_name, media_type, size_bytes, or sha256.");
            }

            var sourceKind = FileArtifactSourceKind.Unspecified;
            if (TryGetProperty(fileRefElement, "sourceKind", "source_kind", out var sourceKindElement))
            {
                if (!TryParseSourceKind(sourceKindElement, out sourceKind))
                {
                    throw new WorkflowFileSubmitArgumentErrorException(
                        "invalid_file_ref",
                        "workflow_file_submit file_ref source_kind is not supported.");
                }
            }

            return new FileArtifactRef
            {
                FileId = OptionalString(fileRefElement, "fileId", "file_id"),
                ArtifactId = OptionalString(fileRefElement, "artifactId", "artifact_id"),
                SourceKind = sourceKind,
                SourceMessageId = OptionalString(fileRefElement, "sourceMessageId", "source_message_id"),
                SourceResourceKey = OptionalString(fileRefElement, "sourceResourceKey", "source_resource_key"),
                CreatedAtUnixMs = OptionalInt64(fileRefElement, "createdAtUnixMs", "created_at_unix_ms") ?? 0,
                ExpiresAtUnixMs = OptionalInt64(fileRefElement, "expiresAtUnixMs", "expires_at_unix_ms") ?? 0,
                OwnerRunId = OptionalString(fileRefElement, "ownerRunId", "owner_run_id"),
                OwnerScopeId = OptionalString(fileRefElement, "ownerScopeId", "owner_scope_id"),
            };
        }

        private static IReadOnlyDictionary<string, string> ParseFormFields(JsonElement root)
        {
            if (!TryGetProperty(root, "form", "form", out var formElement) ||
                formElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            if (formElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("workflow_file_submit form must be an object.");

            var formFields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in formElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                    throw new ArgumentException("workflow_file_submit form field name cannot be blank.");
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("workflow_file_submit form fields must be strings.");

                var value = Normalize(property.Value.GetString());
                if (value != null)
                    formFields[property.Name] = value;
            }

            return formFields;
        }

        private static string? ParseOutputKind(JsonElement root)
        {
            if (!TryGetProperty(root, "output", "output", out var outputElement) ||
                outputElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            if (outputElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("workflow_file_submit output must be an object.");

            return OptionalString(outputElement, "kind", "kind");
        }

        private static string? ParseOutputSelector(JsonElement root)
        {
            if (!TryGetProperty(root, "output", "output", out var outputElement) ||
                outputElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            if (outputElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("workflow_file_submit output must be an object.");

            var selector = OptionalString(outputElement, "selector", "selector");
            if (selector != null && !IsSafeOutputSelector(selector))
            {
                throw new WorkflowFileSubmitArgumentErrorException(
                    "invalid_destination",
                    "workflow_file_submit output.selector must be a safe property path.");
            }

            return selector;
        }

        private static bool TryParseSourceKind(
            JsonElement element,
            out FileArtifactSourceKind sourceKind)
        {
            sourceKind = FileArtifactSourceKind.Unspecified;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric))
            {
                if (!Enum.IsDefined(typeof(FileArtifactSourceKind), numeric))
                    return false;

                sourceKind = (FileArtifactSourceKind)numeric;
                return true;
            }

            if (element.ValueKind != JsonValueKind.String)
                return false;

            sourceKind = NormalizeKey(element.GetString()) switch
            {
                "unspecified" => FileArtifactSourceKind.Unspecified,
                "chatinput" => FileArtifactSourceKind.ChatInput,
                "formupload" => FileArtifactSourceKind.FormUpload,
                "connectedserviceresource" => FileArtifactSourceKind.ConnectedServiceResource,
                "externalresource" => FileArtifactSourceKind.ExternalResource,
                "generated" => FileArtifactSourceKind.Generated,
                _ => FileArtifactSourceKind.Unspecified,
            };
            return sourceKind != FileArtifactSourceKind.Unspecified ||
                   NormalizeKey(element.GetString()) == "unspecified";
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

        private static string RequiredString(JsonElement source, string camelCaseName, string snakeCaseName) =>
            OptionalString(source, camelCaseName, snakeCaseName)
            ?? throw new WorkflowFileSubmitArgumentErrorException(
                "invalid_destination",
                $"workflow_file_submit requires {snakeCaseName}.");

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

        private static WorkflowToolExecutionResult Error(
            CandidateDestination? destination,
            string error,
            string detail,
            int? httpStatus = null,
            int? providerCode = null)
        {
            var resultJson = JsonSerializer.Serialize(
                new WorkflowFileSubmitResult(
                    Success: false,
                    Error: error,
                    Detail: detail,
                    OutputCode: null,
                    OutputKind: null,
                    HttpStatus: httpStatus,
                    ProviderCode: providerCode,
                    Destination: destination,
                    File: null),
                JsonOptions);
            return WorkflowToolExecutionResult.Failed(resultJson, error, detail);
        }

        private static bool HasStableFileRef(FileArtifactRef fileRef) =>
            !string.IsNullOrWhiteSpace(fileRef.FileId) ||
            !string.IsNullOrWhiteSpace(fileRef.ArtifactId);

        private static bool HasPublicFileFacts(FileArtifactRef fileRef) =>
            !string.IsNullOrWhiteSpace(fileRef.FileName) ||
            !string.IsNullOrWhiteSpace(fileRef.MediaType) ||
            fileRef.SizeBytes != 0 ||
            !string.IsNullOrWhiteSpace(fileRef.Sha256);

        private static bool MatchesAnyRunContext(string ownerRunId, WorkflowToolExecutionRequest request) =>
            string.Equals(ownerRunId, Normalize(request.RunId), StringComparison.Ordinal) ||
            string.Equals(ownerRunId, Normalize(request.RuntimeContext.ParentRunId), StringComparison.Ordinal) ||
            string.Equals(ownerRunId, Normalize(request.RuntimeContext.RootRunId), StringComparison.Ordinal);

        private static bool MatchesScopeContext(string ownerScopeId, WorkflowToolExecutionRequest request) =>
            string.Equals(ownerScopeId, Normalize(request.ScopeId), StringComparison.Ordinal);

        private static bool DescriptorOwnerMatches(
            FileArtifactRef descriptor,
            WorkflowToolExecutionRequest request)
        {
            var ownerRunId = Normalize(descriptor.OwnerRunId);
            if (ownerRunId == null || !MatchesAnyRunContext(ownerRunId, request))
                return false;

            var ownerScopeId = Normalize(descriptor.OwnerScopeId);
            return ownerScopeId == null || MatchesScopeContext(ownerScopeId, request);
        }

        private static bool DescriptorMatchesValidatedDescriptor(
            FileArtifactRef opened,
            FileArtifactRef validated) =>
            string.Equals(Normalize(opened.FileId), Normalize(validated.FileId), StringComparison.Ordinal) &&
            string.Equals(Normalize(opened.ArtifactId), Normalize(validated.ArtifactId), StringComparison.Ordinal) &&
            opened.SizeBytes == validated.SizeBytes &&
            string.Equals(Normalize(opened.Sha256), Normalize(validated.Sha256), StringComparison.Ordinal) &&
            string.Equals(Normalize(opened.OwnerRunId), Normalize(validated.OwnerRunId), StringComparison.Ordinal) &&
            string.Equals(Normalize(opened.OwnerScopeId), Normalize(validated.OwnerScopeId), StringComparison.Ordinal);

        private static WorkflowFileMultipartUploadExecutionContext ToUploadContext(
            WorkflowToolExecutionRequest request) =>
            new(
                request.RunId,
                Normalize(request.RuntimeContext.ParentRunId),
                Normalize(request.RuntimeContext.RootRunId),
                request.ScopeId,
                request.StepId,
                request.ExecutionId,
                request.CallId,
                request.IdempotencyKey);

        private static bool IsValidRelativePath(string? path)
        {
            var normalized = Normalize(path);
            if (normalized == null)
                return false;
            if (normalized.Contains("://", StringComparison.Ordinal))
                return false;
            if (normalized.Contains(':', StringComparison.Ordinal))
                return false;
            if (normalized.StartsWith("//", StringComparison.Ordinal))
                return false;

            foreach (var segment in normalized.Split('/', StringSplitOptions.TrimEntries))
            {
                if (segment is "." or "..")
                    return false;
            }

            return true;
        }

        private static bool IsSupportedMethod(string? method) =>
            string.Equals(method?.Trim(), "POST", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method?.Trim(), "PUT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method?.Trim(), "PATCH", StringComparison.OrdinalIgnoreCase);

        private static bool IsSafeOutputSelector(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
                return false;

            foreach (var segment in selector.Split('.', StringSplitOptions.TrimEntries))
            {
                if (segment.Length == 0 || IsForbiddenOutputSelectorSegment(segment))
                    return false;
                foreach (var c in segment)
                {
                    if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                        return false;
                }
            }

            return true;
        }

        private static bool IsForbiddenOutputSelectorSegment(string segment) =>
            ForbiddenOutputSelectorSegmentNames.Contains(NormalizeKey(segment));

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

        private static readonly HashSet<string> ForbiddenTopLevelArgumentNames = new(StringComparer.Ordinal)
        {
            "target",
            "headers",
            "body",
            "bytes",
            "raw_body",
            "base64",
            "data_uri",
        };

        private static readonly HashSet<string> ForbiddenOutputSelectorSegmentNames = new(StringComparer.Ordinal)
        {
            "raw",
            "body",
            "bytes",
            "rawbody",
            "base64",
            "database64",
            "datauri",
        };
    }

    private sealed record WorkflowFileSubmitArguments(
        FileArtifactRef FileRef,
        string ServiceSlug,
        string Path,
        string Method,
        string FileFieldName,
        IReadOnlyDictionary<string, string> FormFields,
        string? OutputKind,
        string? OutputSelector,
        long? MaxFileBytes);

    private sealed record WorkflowFileSubmitResult(
        bool Success,
        string? Error,
        string? Detail,
        string? OutputCode,
        string? OutputKind,
        int? HttpStatus,
        int? ProviderCode,
        CandidateDestination? Destination,
        WorkflowFileSubmitFile? File);

    private sealed record CandidateDestination(
        string Slug,
        string Path,
        string Method)
    {
        public static CandidateDestination From(WorkflowFileSubmitArguments arguments) =>
            new(arguments.ServiceSlug, arguments.Path, arguments.Method);

        public static CandidateDestination From(WorkflowFileMultipartUploadPolicy policy) =>
            new(policy.ServiceSlug, policy.Path, policy.Method);
    }

    private sealed record WorkflowFileSubmitFile(
        string? FileId,
        string? ArtifactId,
        string? FileName,
        string? MediaType,
        long SizeBytes,
        string? Sha256);

    private sealed class WorkflowFileSubmitArgumentErrorException(
        string error,
        string detail) : ArgumentException(detail)
    {
        public string Error { get; } = error;
        public string Detail { get; } = detail;
    }
}
