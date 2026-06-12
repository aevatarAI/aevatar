using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using UglyToad.PdfPig;
using ProtoWorkflowFileRef = Aevatar.Workflow.Abstractions.WorkflowFileRef;
using ProtoWorkflowFileSourceKind = Aevatar.Workflow.Abstractions.WorkflowFileSourceKind;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowDocumentExtractToolSource(IWorkflowFileArtifactReadPort fileArtifacts) : IWorkflowToolSource
{
    private readonly IWorkflowFileArtifactReadPort _fileArtifacts =
        fileArtifacts ?? throw new ArgumentNullException(nameof(fileArtifacts));

    public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IWorkflowTool>>([new DocumentExtractTool(_fileArtifacts)]);

    private sealed class DocumentExtractTool(IWorkflowFileArtifactReadPort fileArtifacts) : IWorkflowTool
    {
        private const int DefaultMaxChars = 20_000;
        private const int HardMaxChars = 100_000;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IWorkflowFileArtifactReadPort _fileArtifacts = fileArtifacts;

        public string Name => "document_extract";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            ExecuteAsync(new WorkflowToolExecutionRequest(argumentsJson), ct);

        public async Task<string> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            try
            {
                var arguments = ParseArguments(request);
                var artifact = await _fileArtifacts.OpenReadAsync(arguments.FileRef, ct)
                    .ConfigureAwait(false);
                await using (artifact.Content.ConfigureAwait(false))
                {
                    var descriptor = artifact.FileRef;
                    var mediaType = NormalizeMediaType(descriptor.MediaType);
                    if (mediaType == null)
                        return Error("unsupported_media_type", "Workflow file media type is required.");

                    if (!SupportedMediaTypes.Contains(mediaType))
                        return Error(
                            "unsupported_media_type",
                            $"document_extract does not support media type '{mediaType}'.");

                    var maxChars = NormalizeMaxChars(arguments.MaxChars);
                    var extracted = mediaType == "application/pdf"
                        ? ExtractPdfText(artifact.Content, maxChars)
                        : await ExtractUtf8TextAsync(artifact.Content, maxChars, ct).ConfigureAwait(false);

                    return JsonSerializer.Serialize(
                        new DocumentExtractResult(
                            ExtractionKind: mediaType == "application/pdf" ? "pdf_text" : "utf8_text",
                            MediaType: mediaType,
                            File: ToResultFileRef(descriptor),
                            Text: extracted.Text,
                            Truncated: extracted.Truncated,
                            ExtractedChars: extracted.Text.Length),
                        JsonOptions);
                }
            }
            catch (JsonException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }
            catch (DecoderFallbackException)
            {
                return Error("invalid_text_encoding", "Workflow file text content must be valid UTF-8.");
            }
            catch (ArgumentException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }
            catch (Exception ex) when (IsSafeArtifactFailure(ex))
            {
                return Error("artifact_unavailable", SafeArtifactDetail(ex));
            }
        }

        private static DocumentExtractArguments ParseArguments(WorkflowToolExecutionRequest request)
        {
            var argumentsJson = string.IsNullOrWhiteSpace(request.ArgumentsJson)
                ? "{}"
                : request.ArgumentsJson;
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("document_extract arguments must be a JSON object.");

            var fileRef = TryResolveExplicitFileRef(root, out var explicitFileRef)
                ? explicitFileRef
                : ResolveSingleInputFileRef(request.InputFileRefs);
            if (string.IsNullOrWhiteSpace(fileRef.FileId) &&
                string.IsNullOrWhiteSpace(fileRef.ArtifactId))
                throw new ArgumentException("document_extract fileRef requires fileId or artifactId.");

            int? maxChars = null;
            if (TryGetProperty(root, "maxChars", "max_chars", out var maxCharsElement))
                maxChars = maxCharsElement.ValueKind == JsonValueKind.Number &&
                           maxCharsElement.TryGetInt32(out var parsed)
                    ? parsed
                    : throw new ArgumentException("document_extract maxChars must be an integer.");
            return new DocumentExtractArguments(fileRef, maxChars);
        }

        private static bool TryResolveExplicitFileRef(
            JsonElement root,
            out WorkflowFileRef fileRef)
        {
            fileRef = new WorkflowFileRef();
            if (!TryGetProperty(root, "fileRef", "file_ref", out var fileRefElement))
                return false;
            if (fileRefElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("document_extract fileRef must be an object.");

            fileRef = ParseFileRef(fileRefElement);
            return true;
        }

        private static WorkflowFileRef ResolveSingleInputFileRef(
            IReadOnlyList<ProtoWorkflowFileRef> inputFileRefs)
        {
            if (inputFileRefs.Count == 1)
                return ToApplicationFileRef(inputFileRefs[0]);

            throw new ArgumentException(inputFileRefs.Count == 0
                ? "document_extract requires a fileRef object or exactly one input file ref."
                : "document_extract received multiple input file refs; provide fileRef explicitly.");
        }

        private static WorkflowFileRef ParseFileRef(JsonElement fileRefElement)
        {
            var sourceKind = WorkflowFileSourceKind.Unspecified;
            if (TryGetProperty(fileRefElement, "sourceKind", "source_kind", out var sourceKindElement))
                sourceKind = ParseSourceKind(sourceKindElement);

            return new WorkflowFileRef
            {
                FileId = GetString(fileRefElement, "fileId", "file_id"),
                ArtifactId = GetString(fileRefElement, "artifactId", "artifact_id"),
                SourceKind = sourceKind,
                SourceMessageId = GetString(fileRefElement, "sourceMessageId", "source_message_id"),
                SourceResourceKey = GetString(fileRefElement, "sourceResourceKey", "source_resource_key"),
                FileName = GetString(fileRefElement, "fileName", "file_name"),
                MediaType = GetString(fileRefElement, "mediaType", "media_type"),
                SizeBytes = GetInt64(fileRefElement, "sizeBytes", "size_bytes") ?? 0,
                Sha256 = GetString(fileRefElement, "sha256", "sha256"),
                CreatedAtUnixMs = GetInt64(fileRefElement, "createdAtUnixMs", "created_at_unix_ms") ?? 0,
                ExpiresAtUnixMs = GetInt64(fileRefElement, "expiresAtUnixMs", "expires_at_unix_ms") ?? 0,
                OwnerRunId = GetString(fileRefElement, "ownerRunId", "owner_run_id"),
                OwnerScopeId = GetString(fileRefElement, "ownerScopeId", "owner_scope_id"),
            };
        }

        private static WorkflowFileRef ToApplicationFileRef(ProtoWorkflowFileRef source) =>
            new()
            {
                FileId = Normalize(source.FileId),
                ArtifactId = Normalize(source.ArtifactId),
                SourceKind = source.SourceKind switch
                {
                    ProtoWorkflowFileSourceKind.ChatInput => WorkflowFileSourceKind.ChatInput,
                    ProtoWorkflowFileSourceKind.FormUpload => WorkflowFileSourceKind.FormUpload,
                    ProtoWorkflowFileSourceKind.ConnectedServiceResource =>
                        WorkflowFileSourceKind.ConnectedServiceResource,
                    ProtoWorkflowFileSourceKind.ExternalResource => WorkflowFileSourceKind.ExternalResource,
                    ProtoWorkflowFileSourceKind.Generated => WorkflowFileSourceKind.Generated,
                    _ => WorkflowFileSourceKind.Unspecified,
                },
                SourceMessageId = Normalize(source.SourceMessageId),
                SourceResourceKey = Normalize(source.SourceResourceKey),
                FileName = Normalize(source.FileName),
                MediaType = Normalize(source.MediaType),
                SizeBytes = source.SizeBytes,
                Sha256 = Normalize(source.Sha256),
                CreatedAtUnixMs = source.CreatedAtUnixMs,
                ExpiresAtUnixMs = source.ExpiresAtUnixMs,
                OwnerRunId = Normalize(source.OwnerRunId),
                OwnerScopeId = Normalize(source.OwnerScopeId),
            };

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

        private static string? GetString(JsonElement source, string camelCaseName, string snakeCaseName)
        {
            if (!TryGetProperty(source, camelCaseName, snakeCaseName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            return value.ValueKind == JsonValueKind.String
                ? Normalize(value.GetString())
                : throw new ArgumentException($"document_extract fileRef.{camelCaseName} must be a string.");
        }

        private static long? GetInt64(JsonElement source, string camelCaseName, string snakeCaseName)
        {
            if (!TryGetProperty(source, camelCaseName, snakeCaseName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
                ? parsed
                : throw new ArgumentException($"document_extract fileRef.{camelCaseName} must be an integer.");
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

        private static int NormalizeMaxChars(int? maxChars)
        {
            if (maxChars == null)
                return DefaultMaxChars;
            if (maxChars <= 0)
                throw new ArgumentException("document_extract maxChars must be greater than zero.");
            return Math.Min(maxChars.Value, HardMaxChars);
        }

        private static async Task<ExtractedText> ExtractUtf8TextAsync(
            Stream content,
            int maxChars,
            CancellationToken ct)
        {
            using var reader = new StreamReader(
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            var buffer = new char[maxChars + 1];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct)
                .ConfigureAwait(false);
            var truncated = read > maxChars;
            var length = Math.Min(read, maxChars);
            return new ExtractedText(new string(buffer, 0, length), truncated);
        }

        private static ExtractedText ExtractPdfText(Stream content, int maxChars)
        {
            var builder = new StringBuilder(capacity: Math.Min(maxChars, 4096));
            using var document = PdfDocument.Open(content);
            var truncated = false;
            foreach (var page in document.GetPages())
            {
                var text = page.Text ?? string.Empty;
                if (text.Length == 0)
                    continue;

                if (builder.Length > 0)
                    AppendBounded(builder, Environment.NewLine, maxChars, ref truncated);
                AppendBounded(builder, text, maxChars, ref truncated);
                if (truncated)
                    break;
            }

            return new ExtractedText(builder.ToString(), truncated);
        }

        private static void AppendBounded(
            StringBuilder builder,
            string value,
            int maxChars,
            ref bool truncated)
        {
            if (builder.Length >= maxChars)
            {
                truncated = true;
                return;
            }

            var remaining = maxChars - builder.Length;
            if (value.Length <= remaining)
            {
                builder.Append(value);
                return;
            }

            builder.Append(value, 0, remaining);
            truncated = true;
        }

        private static string? NormalizeMediaType(string? mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
                return null;
            var normalized = mediaType.Trim();
            var separatorIndex = normalized.IndexOf(';', StringComparison.Ordinal);
            if (separatorIndex >= 0)
                normalized = normalized[..separatorIndex].Trim();
            return normalized.ToLowerInvariant();
        }

        private static WorkflowDocumentExtractFileRef ToResultFileRef(WorkflowFileRef descriptor) =>
            new(
                descriptor.FileId,
                descriptor.ArtifactId,
                descriptor.SourceKind.ToString(),
                descriptor.SourceMessageId,
                descriptor.SourceResourceKey,
                descriptor.FileName,
                descriptor.MediaType,
                descriptor.SizeBytes,
                descriptor.Sha256,
                descriptor.CreatedAtUnixMs,
                descriptor.ExpiresAtUnixMs,
                descriptor.OwnerRunId,
                descriptor.OwnerScopeId);

        private static string Error(string error, string detail) =>
            JsonSerializer.Serialize(
                new DocumentExtractError(error, detail),
                JsonOptions);

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
                _ => ex.Message,
            };

        private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.Ordinal)
        {
            "text/plain",
            "application/json",
            "text/markdown",
            "text/csv",
            "application/pdf",
        };
    }

    private sealed record DocumentExtractArguments(WorkflowFileRef FileRef, int? MaxChars = null);

    private sealed record ExtractedText(string Text, bool Truncated);

    private sealed record DocumentExtractResult(
        string ExtractionKind,
        string MediaType,
        WorkflowDocumentExtractFileRef File,
        string Text,
        bool Truncated,
        int ExtractedChars);

    private sealed record WorkflowDocumentExtractFileRef(
        string? FileId,
        string? ArtifactId,
        string SourceKind,
        string? SourceMessageId,
        string? SourceResourceKey,
        string? FileName,
        string? MediaType,
        long SizeBytes,
        string? Sha256,
        long CreatedAtUnixMs,
        long ExpiresAtUnixMs,
        string? OwnerRunId,
        string? OwnerScopeId);

    private sealed record DocumentExtractError(string Error, string Detail);
}
