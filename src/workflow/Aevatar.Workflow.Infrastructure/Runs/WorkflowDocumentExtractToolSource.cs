using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Infrastructure.Runs.DocumentExtraction;
using System.IO.Compression;
using UglyToad.PdfPig;
using ProtoWorkflowFileRef = Aevatar.Workflow.Abstractions.WorkflowFileRef;
using ProtoWorkflowFileSourceKind = Aevatar.Workflow.Abstractions.WorkflowFileSourceKind;
using ProtoWorkflowLlmControlContext = Aevatar.Workflow.Abstractions.WorkflowLlmControlContext;

namespace Aevatar.Workflow.Infrastructure.Runs;

public sealed class WorkflowDocumentExtractToolSource(
    IFileArtifactReadPort fileArtifacts,
    ILLMProvider? llmProvider = null,
    ILLMProviderFactory? llmProviderFactory = null) : IWorkflowToolSource
{
    private readonly IFileArtifactReadPort _fileArtifacts =
        fileArtifacts ?? throw new ArgumentNullException(nameof(fileArtifacts));
    private readonly ILLMProvider? _llmProvider = llmProvider;
    private readonly ILLMProviderFactory? _llmProviderFactory = llmProviderFactory;

    public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IWorkflowTool>>([
            new DocumentExtractTool(_fileArtifacts, _llmProvider, _llmProviderFactory),
        ]);

    private sealed class DocumentExtractTool(
        IFileArtifactReadPort fileArtifacts,
        ILLMProvider? llmProvider,
        ILLMProviderFactory? llmProviderFactory) : IWorkflowTool
    {
        private const int DefaultMaxChars = 20_000;
        private const int HardMaxChars = 100_000;
        private const int MaxImageBytes = 5 * 1024 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IFileArtifactReadPort _fileArtifacts = fileArtifacts;
        private readonly ILLMProvider? _llmProvider = llmProvider;
        private readonly ILLMProviderFactory? _llmProviderFactory = llmProviderFactory;

        public string Name => "document_extract";

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

                    if (!SupportedMediaTypes.Contains(mediaType))
                        return Error(
                            "unsupported_media_type",
                            $"document_extract does not support media type '{mediaType}'.");

                    var maxChars = NormalizeMaxChars(arguments.MaxChars);
                    if (IsSupportedImageMediaType(mediaType))
                    {
                        if (arguments.RequestKind == DocumentExtractionRequestKind.SchemaBoundJson)
                        {
                            return await ExtractImageSchemaBoundJsonAsync(
                                request,
                                artifact.Content,
                                descriptor,
                                mediaType,
                                arguments.SchemaContract!,
                                ct).ConfigureAwait(false);
                        }

                        return await ExtractImageTextAsync(
                            request,
                            artifact.Content,
                            descriptor,
                            mediaType,
                            maxChars,
                            ct).ConfigureAwait(false);
                    }

                    var extracted = mediaType switch
                    {
                        "application/pdf" => ExtractPdfText(artifact.Content, maxChars),
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" =>
                            ExtractDocxText(artifact.Content, maxChars),
                        _ => await ExtractUtf8TextAsync(artifact.Content, maxChars, ct).ConfigureAwait(false),
                    };

                    if (arguments.RequestKind == DocumentExtractionRequestKind.SchemaBoundJson)
                    {
                        return await ExtractSchemaBoundJsonAsync(
                            request,
                            extracted.Text,
                            descriptor,
                            mediaType,
                            arguments.SchemaContract!,
                            imageBytes: null,
                            ct).ConfigureAwait(false);
                    }

                    return WorkflowToolExecutionResult.Success(JsonSerializer.Serialize(
                        new DocumentExtractResult(
                            ExtractionKind: ResolveExtractionKind(mediaType),
                            MediaType: mediaType,
                            File: ToResultFileRef(descriptor),
                            Text: extracted.Text,
                            Truncated: extracted.Truncated,
                            ExtractedChars: extracted.Text.Length),
                        JsonOptions));
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

        private async Task<WorkflowToolExecutionResult> ExtractImageSchemaBoundJsonAsync(
            WorkflowToolExecutionRequest request,
            Stream content,
            FileArtifactRef descriptor,
            string mediaType,
            DocumentSchemaContract schemaContract,
            CancellationToken ct)
        {
            if (descriptor.SizeBytes > MaxImageBytes)
                return Error("image_too_large", $"document_extract image input exceeds {MaxImageBytes} bytes.");

            byte[] imageBytes;
            try
            {
                imageBytes = await ReadCappedImageBytesAsync(content, MaxImageBytes, ct).ConfigureAwait(false);
            }
            catch (ImageTooLargeException)
            {
                return Error("image_too_large", $"document_extract image input exceeds {MaxImageBytes} bytes.");
            }

            return await ExtractSchemaBoundJsonAsync(
                request,
                extractedText: null,
                descriptor,
                mediaType,
                schemaContract,
                imageBytes,
                ct).ConfigureAwait(false);
        }

        private async Task<WorkflowToolExecutionResult> ExtractSchemaBoundJsonAsync(
            WorkflowToolExecutionRequest request,
            string? extractedText,
            FileArtifactRef descriptor,
            string mediaType,
            DocumentSchemaContract schemaContract,
            byte[]? imageBytes,
            CancellationToken ct)
        {
            ILLMProvider? schemaProvider;
            try
            {
                schemaProvider = ResolveProvider();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Error("schema_bound_extraction_failed", "Schema-bound extraction provider failed.");
            }

            if (schemaProvider == null)
            {
                return Error(
                    "schema_bound_provider_unavailable",
                    "document_extract schema-bound extraction requires a configured LLM provider.");
            }

            if (imageBytes != null && !schemaProvider.Capabilities.SupportsInput(ContentPartKind.Image))
            {
                return Error(
                    "image_provider_unavailable",
                    "document_extract image extraction requires a configured LLM provider with image input support.");
            }

            try
            {
                var providerJson = await ExtractSchemaBoundJsonWithProviderAsync(
                    schemaProvider,
                    request,
                    extractedText,
                    imageBytes,
                    descriptor,
                    mediaType,
                    schemaContract,
                    ct).ConfigureAwait(false);
                using var providerDocument = JsonDocument.Parse(providerJson);
                var providerRoot = providerDocument.RootElement;
                if (providerRoot.ValueKind != JsonValueKind.Object)
                    return SchemaValidationError();

                schemaContract.ValidateResult(providerRoot);
                var canonicalResultJson = DocumentSchemaContract.Canonicalize(providerRoot);
                return WorkflowToolExecutionResult.Success(BuildSchemaBoundResultJson(
                    mediaType,
                    descriptor,
                    schemaContract,
                    canonicalResultJson));
            }
            catch (DocumentSchemaValidationException) when (!ct.IsCancellationRequested)
            {
                return SchemaValidationError();
            }
            catch (JsonException) when (!ct.IsCancellationRequested)
            {
                return SchemaValidationError();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Error("schema_bound_extraction_failed", "Schema-bound extraction provider failed.");
            }
        }

        private async Task<WorkflowToolExecutionResult> ExtractImageTextAsync(
            WorkflowToolExecutionRequest request,
            Stream content,
            FileArtifactRef descriptor,
            string mediaType,
            int maxChars,
            CancellationToken ct)
        {
            ILLMProvider? imageProvider;
            try
            {
                imageProvider = ResolveProvider();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Error("image_extraction_failed", "Image extraction provider failed.");
            }

            if (imageProvider == null || !imageProvider.Capabilities.SupportsInput(ContentPartKind.Image))
            {
                return Error(
                    "image_provider_unavailable",
                    "document_extract image extraction requires a configured LLM provider with image input support.");
            }

            if (descriptor.SizeBytes > MaxImageBytes)
                return Error("image_too_large", $"document_extract image input exceeds {MaxImageBytes} bytes.");

            byte[] imageBytes;
            try
            {
                imageBytes = await ReadCappedImageBytesAsync(content, MaxImageBytes, ct).ConfigureAwait(false);
            }
            catch (ImageTooLargeException)
            {
                return Error("image_too_large", $"document_extract image input exceeds {MaxImageBytes} bytes.");
            }

            try
            {
                var extracted = await ExtractImageTextWithProviderAsync(
                    imageProvider,
                    request,
                    imageBytes,
                    descriptor,
                    mediaType,
                    maxChars,
                    ct).ConfigureAwait(false);

                return WorkflowToolExecutionResult.Success(JsonSerializer.Serialize(
                    new DocumentExtractResult(
                        ExtractionKind: ResolveExtractionKind(mediaType),
                        MediaType: mediaType,
                        File: ToResultFileRef(descriptor),
                        Text: extracted.Text,
                        Truncated: extracted.Truncated,
                        ExtractedChars: extracted.Text.Length),
                    JsonOptions));
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return Error("image_extraction_failed", "Image extraction provider failed.");
            }
        }

        private ILLMProvider? ResolveProvider()
        {
            if (_llmProvider != null)
                return _llmProvider;

            return _llmProviderFactory?.GetDefault();
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

            var requestKind = ParseRequestKind(root);
            DocumentSchemaContract? schemaContract = null;
            if (requestKind == DocumentExtractionRequestKind.SchemaBoundJson)
            {
                if (!TryGetProperty(root, "schemaContract", "schema_contract", out var schemaContractElement) ||
                    schemaContractElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    throw new ArgumentException(
                        "document_extract schema_contract is required when extraction_kind is schema_bound_json.");

                schemaContract = DocumentSchemaContract.Parse(schemaContractElement);
            }

            return new DocumentExtractArguments(fileRef, maxChars, requestKind, schemaContract);
        }

        private static DocumentExtractionRequestKind ParseRequestKind(JsonElement root)
        {
            if (!TryGetProperty(root, "extractionKind", "extraction_kind", out var kindElement) ||
                kindElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return DocumentExtractionRequestKind.Text;

            if (kindElement.ValueKind != JsonValueKind.String)
                throw new ArgumentException("document_extract extraction_kind must be a string.");

            return NormalizeKey(kindElement.GetString()) switch
            {
                "" or "text" => DocumentExtractionRequestKind.Text,
                "schemaboundjson" => DocumentExtractionRequestKind.SchemaBoundJson,
                _ => throw new ArgumentException(
                    "document_extract extraction_kind must be 'text' or 'schema_bound_json'."),
            };
        }

        private static bool TryResolveExplicitFileRef(
            JsonElement root,
            out FileArtifactRef fileRef)
        {
            fileRef = new FileArtifactRef();
            if (!TryGetProperty(root, "fileRef", "file_ref", out var fileRefElement))
                return false;
            if (fileRefElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("document_extract fileRef must be an object.");

            fileRef = ParseFileRef(fileRefElement);
            return true;
        }

        private static FileArtifactRef ResolveSingleInputFileRef(
            IReadOnlyList<ProtoWorkflowFileRef> inputFileRefs)
        {
            if (inputFileRefs.Count == 1)
                return ToApplicationFileRef(inputFileRefs[0]);

            throw new ArgumentException(inputFileRefs.Count == 0
                ? "document_extract requires a fileRef object or exactly one input file ref."
                : "document_extract received multiple input file refs; provide fileRef explicitly.");
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

        private async Task<string> ExtractSchemaBoundJsonWithProviderAsync(
            ILLMProvider provider,
            WorkflowToolExecutionRequest workflowRequest,
            string? extractedText,
            byte[]? imageBytes,
            FileArtifactRef descriptor,
            string mediaType,
            DocumentSchemaContract schemaContract,
            CancellationToken ct)
        {
            var request = new LLMRequest
            {
                Messages = BuildSchemaBoundMessages(extractedText, imageBytes, descriptor, mediaType, schemaContract),
                RequestId = $"document_extract:{descriptor.FileId ?? descriptor.ArtifactId ?? "schema"}",
                CallerContext = ToCallerContext(workflowRequest),
                LlmControl = ToLlmControl(workflowRequest.LlmControl),
                ResponseFormat = LLMResponseFormat.ForJsonSchema(
                    schemaContract.Schema,
                    schemaContract.Name,
                    schemaContract.Description),
            };

            var builder = new StringBuilder();
            await foreach (var chunk in provider.ChatStreamAsync(request, ct).WithCancellation(ct)
                               .ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(chunk.DeltaContent))
                    builder.Append(chunk.DeltaContent);
            }

            return builder.ToString();
        }

        private static List<ChatMessage> BuildSchemaBoundMessages(
            string? extractedText,
            byte[]? imageBytes,
            FileArtifactRef descriptor,
            string mediaType,
            DocumentSchemaContract schemaContract)
        {
            var instruction = BuildSchemaBoundInstruction(schemaContract);
            if (imageBytes == null)
            {
                return
                [
                    ChatMessage.System(instruction),
                    ChatMessage.User(BuildTextSchemaBoundUserMessage(extractedText ?? string.Empty, schemaContract)),
                ];
            }

            return
            [
                ChatMessage.System(instruction),
                ChatMessage.User(
                [
                    ContentPart.TextPart(BuildImageSchemaBoundUserMessage(schemaContract)),
                    ContentPart.ImagePart(
                        Convert.ToBase64String(imageBytes),
                        mediaType,
                        descriptor.FileName),
                ]),
            ];
        }

        private static string BuildSchemaBoundInstruction(DocumentSchemaContract schemaContract) =>
            string.Join(
                Environment.NewLine,
                "Extract facts from the provided document and return a JSON object that satisfies the supplied schema.",
                "Do not include markdown, prose, raw document bytes, base64, data URIs, prompt text, or fields outside the schema.",
                $"Schema name: {schemaContract.Name}",
                $"Schema hash: {schemaContract.Hash}",
                $"Schema JSON: {schemaContract.CanonicalSchemaJson}");

        private static string BuildTextSchemaBoundUserMessage(
            string extractedText,
            DocumentSchemaContract schemaContract) =>
            string.Join(
                Environment.NewLine,
                $"Schema name: {schemaContract.Name}",
                "Document text:",
                extractedText);

        private static string BuildImageSchemaBoundUserMessage(DocumentSchemaContract schemaContract) =>
            string.Join(
                Environment.NewLine,
                $"Schema name: {schemaContract.Name}",
                "Read the attached image and return only the schema-conforming JSON object.");

        private async Task<ExtractedText> ExtractImageTextWithProviderAsync(
            ILLMProvider imageProvider,
            WorkflowToolExecutionRequest workflowRequest,
            byte[] imageBytes,
            FileArtifactRef descriptor,
            string mediaType,
            int maxChars,
            CancellationToken ct)
        {
            var request = new LLMRequest
            {
                Messages =
                [
                    ChatMessage.System("Extract readable text from the image. Return only the extracted text."),
                    ChatMessage.User(
                    [
                        ContentPart.ImagePart(
                            Convert.ToBase64String(imageBytes),
                            mediaType,
                            descriptor.FileName),
                    ]),
                ],
                RequestId = $"document_extract:{descriptor.FileId ?? descriptor.ArtifactId ?? "image"}",
                CallerContext = ToCallerContext(workflowRequest),
                LlmControl = ToLlmControl(workflowRequest.LlmControl),
            };

            var builder = new StringBuilder(capacity: Math.Min(maxChars, 4096));
            var truncated = false;
            await foreach (var chunk in imageProvider.ChatStreamAsync(request, ct).WithCancellation(ct)
                               .ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(chunk.DeltaContent))
                    continue;

                AppendBounded(builder, chunk.DeltaContent, maxChars, ref truncated);
            }

            return new ExtractedText(builder.ToString(), truncated);
        }

        private static LLMRequestCallerContext ToCallerContext(WorkflowToolExecutionRequest request)
        {
            var scopeId = Normalize(request.ScopeId) ?? string.Empty;
            var bearer = Normalize(request.CallerCredential?.BearerToken);
            return new LLMRequestCallerContext(
                scopeId,
                scopeId,
                ResponseId: null,
                bearer is null ? null : new LLMRequestCallerCredentials(bearer));
        }

        private static LLMControlContext? ToLlmControl(ProtoWorkflowLlmControlContext? source) =>
            source is null
                ? null
                : new LLMControlContext(
                    NyxIdAccessToken: null,
                    NyxIdOrgToken: null,
                    SenderNyxIdAccessToken: Normalize(source.SenderNyxIdAccessToken),
                    ModelOverride: Normalize(source.ModelOverride),
                    NyxIdRoutePreference: Normalize(source.RoutePreference),
                    MaxToolRoundsOverride: source.HasMaxToolRoundsOverride
                        ? source.MaxToolRoundsOverride
                        : null,
                    UserMemoryPrompt: Normalize(source.UserMemoryPrompt));

        private static async Task<byte[]> ReadCappedImageBytesAsync(
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
                    throw new ImageTooLargeException();

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
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

        private static ExtractedText ExtractDocxText(Stream content, int maxChars)
        {
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: false);
            var documentEntry = archive.GetEntry("word/document.xml")
                ?? throw new InvalidOperationException("DOCX document body was not found.");

            var builder = new StringBuilder(capacity: Math.Min(maxChars, 4096));
            var truncated = false;
            using var documentStream = documentEntry.Open();
            using var xmlReader = XmlReader.Create(documentStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            var document = XDocument.Load(xmlReader, LoadOptions.None);
            XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

            foreach (var paragraph in document.Descendants(word + "p"))
            {
                var paragraphText = string.Concat(paragraph.Descendants(word + "t")
                    .Select(element => element.Value));
                if (paragraphText.Length == 0)
                    continue;

                if (builder.Length > 0)
                    AppendBounded(builder, Environment.NewLine, maxChars, ref truncated);
                AppendBounded(builder, paragraphText, maxChars, ref truncated);
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

        private static bool IsSupportedImageMediaType(string mediaType) =>
            mediaType is "image/png" or "image/jpeg";

        private static string ResolveExtractionKind(string mediaType) =>
            mediaType switch
            {
                "application/pdf" => "pdf_text",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx_text",
                "image/png" or "image/jpeg" => "image_text",
                _ => "utf8_text",
            };

        private static WorkflowDocumentExtractFileRef ToResultFileRef(FileArtifactRef descriptor) =>
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
                new DocumentExtractError(error, detail),
                JsonOptions);
            return WorkflowToolExecutionResult.Failed(resultJson, error, detail);
        }

        private static WorkflowToolExecutionResult SchemaValidationError() =>
            Error("schema_bound_validation_failed", "Schema-bound extraction result failed validation.");

        private static string BuildSchemaBoundResultJson(
            string mediaType,
            FileArtifactRef descriptor,
            DocumentSchemaContract schemaContract,
            string canonicalResultJson)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("extraction_kind", "schema_bound_json");
                writer.WriteString("media_type", mediaType);
                writer.WritePropertyName("file");
                JsonSerializer.Serialize(writer, ToResultFileRef(descriptor), JsonOptions);
                writer.WriteString("schema_name", schemaContract.Name);
                writer.WriteString("schema_hash", schemaContract.Hash);
                writer.WritePropertyName("structured_result");
                using (var resultDocument = JsonDocument.Parse(canonicalResultJson))
                {
                    resultDocument.RootElement.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
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
                _ => ex.Message,
            };

        private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.Ordinal)
        {
            "text/plain",
            "application/json",
            "text/markdown",
            "text/csv",
            "application/pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "image/png",
            "image/jpeg",
        };
    }

    private sealed class ImageTooLargeException : Exception;

    private sealed record DocumentExtractArguments(
        FileArtifactRef FileRef,
        int? MaxChars = null,
        DocumentExtractionRequestKind RequestKind = DocumentExtractionRequestKind.Text,
        DocumentSchemaContract? SchemaContract = null);

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
