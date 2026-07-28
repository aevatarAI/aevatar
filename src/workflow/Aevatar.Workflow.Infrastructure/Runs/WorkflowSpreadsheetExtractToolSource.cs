using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Microsoft.Extensions.Options;
using ProtoWorkflowFileRef = Aevatar.Workflow.Abstractions.WorkflowFileRef;
using ProtoWorkflowFileSourceKind = Aevatar.Workflow.Abstractions.WorkflowFileSourceKind;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowSpreadsheetExtractToolSource(
    IFileArtifactReadPort fileArtifacts,
    IOptions<WorkflowSpreadsheetExtractOptions> options) : IWorkflowToolSource
{
    private readonly IFileArtifactReadPort _fileArtifacts =
        fileArtifacts ?? throw new ArgumentNullException(nameof(fileArtifacts));
    private readonly IOptions<WorkflowSpreadsheetExtractOptions> _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IWorkflowTool>>([
            new SpreadsheetExtractTool(_fileArtifacts, _options),
        ]);

    private sealed class SpreadsheetExtractTool(
        IFileArtifactReadPort fileArtifacts,
        IOptions<WorkflowSpreadsheetExtractOptions> options) : IWorkflowTool
    {
        private const string XlsxMediaType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IFileArtifactReadPort _fileArtifacts = fileArtifacts;
        private readonly IOptions<WorkflowSpreadsheetExtractOptions> _options = options;

        public string Name => "spreadsheet_extract";

        public async Task<WorkflowToolExecutionResult> ExecuteAsync(
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

                    if (!string.Equals(mediaType, XlsxMediaType, StringComparison.Ordinal))
                    {
                        return Error(
                            "unsupported_media_type",
                            $"spreadsheet_extract does not support media type '{mediaType}'.");
                    }

                    if (!HasSupportedXlsxFileName(descriptor.FileName))
                    {
                        return Error(
                            "unsupported_file_type",
                            "spreadsheet_extract only supports .xlsx workbook files.");
                    }

                    var normalizedOptions = NormalizeOptions(_options.Value);
                    if (descriptor.SizeBytes > normalizedOptions.MaxWorkbookBytes)
                    {
                        return Error(
                            "workbook_too_large",
                            $"Spreadsheet workbook exceeds {normalizedOptions.MaxWorkbookBytes} bytes.");
                    }

                    var workbookBytes = await ReadCappedWorkbookBytesAsync(
                        artifact.Content,
                        normalizedOptions.MaxWorkbookBytes,
                        ct).ConfigureAwait(false);
                    var preview = new SpreadsheetPreviewExtractor(normalizedOptions).Extract(workbookBytes);
                    return WorkflowToolExecutionResult.Success(JsonSerializer.Serialize(
                        new SpreadsheetExtractResult(
                            Kind: "spreadsheet_preview",
                            MediaType: mediaType,
                            File: ToResultFileRef(descriptor),
                            Limits: preview.Limits,
                            Workbook: preview.Workbook,
                            Sheets: preview.Sheets,
                            Truncated: preview.Truncated),
                        JsonOptions));
                }
            }
            catch (JsonException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Error("invalid_arguments", ex.Message);
            }
            catch (SpreadsheetTooLargeException ex)
            {
                return Error("workbook_too_large", ex.Message);
            }
            catch (SpreadsheetPreviewException ex)
            {
                return Error(ToErrorName(ex.ErrorCode), ex.Message);
            }
            catch (InvalidDataException)
            {
                return Error("invalid_workbook", "Spreadsheet workbook package is invalid.");
            }
            catch (XmlException)
            {
                return Error("invalid_workbook", "Spreadsheet workbook XML is invalid.");
            }
            catch (Exception ex) when (IsSafeArtifactFailure(ex))
            {
                return Error("artifact_unavailable", SafeArtifactDetail(ex));
            }
        }

        private static SpreadsheetExtractArguments ParseArguments(WorkflowToolExecutionRequest request)
        {
            var argumentsJson = string.IsNullOrWhiteSpace(request.ArgumentsJson)
                ? "{}"
                : request.ArgumentsJson;
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("spreadsheet_extract arguments must be a JSON object.");

            var fileRef = TryResolveExplicitFileRef(root, out var explicitFileRef)
                ? explicitFileRef
                : ResolveSingleInputFileRef(request.InputFileRefs);
            if (string.IsNullOrWhiteSpace(fileRef.FileId) &&
                string.IsNullOrWhiteSpace(fileRef.ArtifactId))
                throw new ArgumentException("spreadsheet_extract fileRef requires fileId or artifactId.");

            return new SpreadsheetExtractArguments(fileRef);
        }

        private static bool TryResolveExplicitFileRef(
            JsonElement root,
            out FileArtifactRef fileRef)
        {
            fileRef = new FileArtifactRef();
            if (!TryGetProperty(root, "fileRef", "file_ref", out var fileRefElement))
                return false;
            if (fileRefElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("spreadsheet_extract fileRef must be an object.");

            fileRef = ParseFileRef(fileRefElement);
            return true;
        }

        private static FileArtifactRef ResolveSingleInputFileRef(
            IReadOnlyList<ProtoWorkflowFileRef> inputFileRefs)
        {
            if (inputFileRefs.Count == 1)
                return ToApplicationFileRef(inputFileRefs[0]);

            throw new ArgumentException(inputFileRefs.Count == 0
                ? "spreadsheet_extract requires a fileRef object or exactly one input file ref."
                : "spreadsheet_extract received multiple input file refs; provide fileRef explicitly.");
        }

        private static FileArtifactRef ToApplicationFileRef(ProtoWorkflowFileRef source) =>
            new()
            {
                FileId = Normalize(source.FileId),
                ArtifactId = Normalize(source.ArtifactId),
                SourceKind = source.SourceKind switch
                {
                    ProtoWorkflowFileSourceKind.ChatInput => FileArtifactSourceKind.ChatInput,
                    ProtoWorkflowFileSourceKind.FormUpload => FileArtifactSourceKind.FormUpload,
                    ProtoWorkflowFileSourceKind.ConnectedServiceResource =>
                        FileArtifactSourceKind.ConnectedServiceResource,
                    ProtoWorkflowFileSourceKind.ExternalResource => FileArtifactSourceKind.ExternalResource,
                    ProtoWorkflowFileSourceKind.Generated => FileArtifactSourceKind.Generated,
                    _ => FileArtifactSourceKind.Unspecified,
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

        private static FileArtifactRef ParseFileRef(JsonElement fileRefElement)
        {
            var sourceKind = FileArtifactSourceKind.Unspecified;
            if (TryGetProperty(fileRefElement, "sourceKind", "source_kind", out var sourceKindElement))
                sourceKind = ParseSourceKind(sourceKindElement);

            return new FileArtifactRef
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
            };
        }

        private static FileArtifactSourceKind ParseSourceKind(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric))
                return Enum.IsDefined(typeof(FileArtifactSourceKind), numeric)
                    ? (FileArtifactSourceKind)numeric
                    : FileArtifactSourceKind.Unspecified;

            if (element.ValueKind != JsonValueKind.String)
                return FileArtifactSourceKind.Unspecified;

            return NormalizeKey(element.GetString()) switch
            {
                "chatinput" => FileArtifactSourceKind.ChatInput,
                "formupload" => FileArtifactSourceKind.FormUpload,
                "connectedserviceresource" => FileArtifactSourceKind.ConnectedServiceResource,
                "externalresource" => FileArtifactSourceKind.ExternalResource,
                "generated" => FileArtifactSourceKind.Generated,
                _ => FileArtifactSourceKind.Unspecified,
            };
        }

        private static string? GetString(JsonElement source, string camelCaseName, string snakeCaseName)
        {
            if (!TryGetProperty(source, camelCaseName, snakeCaseName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            return value.ValueKind == JsonValueKind.String
                ? Normalize(value.GetString())
                : throw new ArgumentException($"spreadsheet_extract fileRef.{camelCaseName} must be a string.");
        }

        private static long? GetInt64(JsonElement source, string camelCaseName, string snakeCaseName)
        {
            if (!TryGetProperty(source, camelCaseName, snakeCaseName, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
                ? parsed
                : throw new ArgumentException($"spreadsheet_extract fileRef.{camelCaseName} must be an integer.");
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

        private static async Task<byte[]> ReadCappedWorkbookBytesAsync(
            Stream content,
            int maxBytes,
            CancellationToken ct)
        {
            using var buffer = new MemoryStream(capacity: Math.Min(maxBytes, 81920));
            var chunk = new byte[81920];
            while (true)
            {
                var read = await content.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                if (buffer.Length + read > maxBytes)
                    throw new SpreadsheetTooLargeException($"Spreadsheet workbook exceeds {maxBytes} bytes.");

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        private static WorkflowSpreadsheetExtractOptions NormalizeOptions(WorkflowSpreadsheetExtractOptions options) =>
            new()
            {
                MaxWorkbookBytes = PositiveOrDefault(options.MaxWorkbookBytes, 5 * 1024 * 1024),
                MaxPackageEntries = PositiveOrDefault(options.MaxPackageEntries, 256),
                MaxPackageEntryBytes = PositiveOrDefault(options.MaxPackageEntryBytes, 1024 * 1024),
                MaxSheets = PositiveOrDefault(options.MaxSheets, 5),
                MaxRowsPerSheet = PositiveOrDefault(options.MaxRowsPerSheet, 50),
                MaxColumnsPerRow = PositiveOrDefault(options.MaxColumnsPerRow, 20),
                MaxCellChars = PositiveOrDefault(options.MaxCellChars, 200),
                MaxSharedStrings = PositiveOrDefault(options.MaxSharedStrings, 10_000),
            };

        private static int PositiveOrDefault(int value, int defaultValue) =>
            value > 0 ? value : defaultValue;

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
            return normalized.ToLowerInvariant();
        }

        private static bool HasSupportedXlsxFileName(string? fileName)
        {
            var normalized = Normalize(fileName);
            return normalized == null ||
                string.Equals(Path.GetExtension(normalized), ".xlsx", StringComparison.OrdinalIgnoreCase);
        }

        private static WorkflowSpreadsheetExtractFileRef ToResultFileRef(FileArtifactRef descriptor) =>
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

        private static WorkflowToolExecutionResult Error(string error, string detail)
        {
            var resultJson = JsonSerializer.Serialize(
                new SpreadsheetExtractError(error, detail),
                JsonOptions);
            return WorkflowToolExecutionResult.Failed(resultJson, error, detail);
        }

        private static string ToErrorName(SpreadsheetPreviewErrorCode errorCode) =>
            errorCode switch
            {
                SpreadsheetPreviewErrorCode.EncryptedWorkbook => "encrypted_workbook",
                SpreadsheetPreviewErrorCode.UnsafeWorkbook => "unsafe_workbook",
                SpreadsheetPreviewErrorCode.WorkbookTooLarge => "workbook_too_large",
                SpreadsheetPreviewErrorCode.UnsupportedWorkbook => "unsupported_workbook",
                _ => "invalid_workbook",
            };

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
    }

    private sealed class SpreadsheetTooLargeException(string message) : Exception(message);

    private sealed record SpreadsheetExtractArguments(FileArtifactRef FileRef);

    private sealed record SpreadsheetExtractResult(
        string Kind,
        string MediaType,
        WorkflowSpreadsheetExtractFileRef File,
        WorkflowSpreadsheetPreviewLimits Limits,
        WorkflowSpreadsheetWorkbookPreview Workbook,
        IReadOnlyList<SpreadsheetSheetPreview> Sheets,
        bool Truncated);

    private sealed record WorkflowSpreadsheetExtractFileRef(
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

    private sealed record SpreadsheetExtractError(string Error, string Detail);
}
