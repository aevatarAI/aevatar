using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class WorkflowMultipartChatRequestParser
{
    private readonly IWorkflowFileIngressPort _fileIngressPort;
    private readonly IOptions<WorkflowMultipartFileIngressOptions> _multipartOptions;
    private readonly IOptions<WorkflowFormFileIngressOptions> _formOptions;

    public WorkflowMultipartChatRequestParser(
        IWorkflowFileIngressPort fileIngressPort,
        IOptions<WorkflowMultipartFileIngressOptions> multipartOptions,
        IOptions<WorkflowFormFileIngressOptions>? formOptions = null)
    {
        _fileIngressPort = fileIngressPort;
        _multipartOptions = multipartOptions;
        _formOptions = formOptions ?? Options.Create(new WorkflowFormFileIngressOptions());
    }

    public async ValueTask<WorkflowMultipartChatRequestParseResult> ParseAsync(
        HttpContext http,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!IsMultipartForm(http.Request.ContentType))
            return WorkflowMultipartChatRequestParseResult.Failed(
                WorkflowMultipartChatRequestParseError.UnsupportedMediaType);

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return WorkflowMultipartChatRequestParseResult.Failed(
                WorkflowMultipartChatRequestParseError.InvalidRequest);
        }

        var formOptions = _formOptions.Value;
        var filesResult = ResolveFiles(form, formOptions.FileFieldName);
        if (!filesResult.Succeeded)
            return WorkflowMultipartChatRequestParseResult.Failed(filesResult.Error!.Value);

        var payloadResult = ParsePayload(form, formOptions.PayloadFieldName);
        if (!payloadResult.Succeeded)
            return WorkflowMultipartChatRequestParseResult.Failed(payloadResult.Error!.Value);

        var source = payloadResult.Input ?? new ChatInput();
        if (ContainsActorFacingFilePayload(source))
            return WorkflowMultipartChatRequestParseResult.Failed(WorkflowMultipartChatRequestParseError.InvalidFileInput);

        var files = filesResult.Files!;
        var inputPartTypes = new string[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (!TryResolveInputPartType(file.ContentType, out var inputPartType))
                return WorkflowMultipartChatRequestParseResult.Failed(
                    WorkflowMultipartChatRequestParseError.InvalidFileInput);

            var validationError = ValidateFile(file);
            if (validationError != null)
                return WorkflowMultipartChatRequestParseResult.Failed(validationError.Value);

            inputPartTypes[i] = inputPartType;
        }

        var ownerScopeId = ResolveScalar(form, "scopeId");
        var inputParts = new List<ChatInputContentPart>(source.InputParts ?? []);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var content = await ReadFileContentAsync(file, cancellationToken);
            if (content.Length == 0)
                return WorkflowMultipartChatRequestParseResult.Failed(
                    WorkflowMultipartChatRequestParseError.InvalidFileInput);

            WorkflowFileIngressResult ingressResult;
            try
            {
                ingressResult = await _fileIngressPort.IngestAsync(
                    new WorkflowFileIngressRequest(
                        content,
                        WorkflowFileSourceKind.FormUpload,
                        FileName: Normalize(file.FileName),
                        MediaType: Normalize(file.ContentType),
                        OwnerScopeId: ownerScopeId),
                    cancellationToken);
            }
            catch (ArgumentException)
            {
                return WorkflowMultipartChatRequestParseResult.Failed(
                    WorkflowMultipartChatRequestParseError.InvalidFileInput);
            }
            catch (InvalidOperationException)
            {
                return WorkflowMultipartChatRequestParseResult.Failed(
                    WorkflowMultipartChatRequestParseError.InvalidFileInput);
            }
            catch (IOException)
            {
                return WorkflowMultipartChatRequestParseResult.Failed(
                    WorkflowMultipartChatRequestParseError.InvalidFileInput);
            }

            var uploadedFileRef = ToInputFileRef(ingressResult.FileRef);
            inputParts.Add(new ChatInputContentPart
            {
                Type = inputPartTypes[i],
                MediaType = uploadedFileRef.MediaType,
                Uri = uploadedFileRef.ArtifactId ?? uploadedFileRef.Uri,
                Name = uploadedFileRef.FileName ?? uploadedFileRef.Name,
                FileRef = uploadedFileRef,
            });
        }

        return WorkflowMultipartChatRequestParseResult.Success(source with
        {
            Prompt = ResolveScalar(form, "prompt") ?? source.Prompt,
            Workflow = ResolveScalar(form, "workflow") ?? source.Workflow,
            SessionId = ResolveScalar(form, "sessionId") ?? source.SessionId,
            ScopeId = ownerScopeId ?? source.ScopeId,
            WorkflowYaml = ResolveScalar(form, "workflowYaml") ?? source.WorkflowYaml,
            WorkflowYamls = ResolveWorkflowYamls(form) ?? source.WorkflowYamls,
            InputParts = inputParts,
        });
    }

    private WorkflowMultipartChatRequestParseError? ValidateFile(IFormFile file)
    {
        if (file.Length <= 0)
            return WorkflowMultipartChatRequestParseError.InvalidFileInput;

        var options = _multipartOptions.Value;
        if (options.MaxFileBytes <= 0 || file.Length > options.MaxFileBytes)
            return WorkflowMultipartChatRequestParseError.InvalidFileInput;

        var mediaType = Normalize(file.ContentType);
        if (mediaType == null || !IsAllowedMediaType(mediaType, options.AllowedMediaTypes))
            return WorkflowMultipartChatRequestParseError.InvalidFileInput;

        return null;
    }

    private static FileListResult ResolveFiles(IFormCollection form, string fileFieldName)
    {
        var expectedName = Normalize(fileFieldName) ?? "file";
        if (form.Files.Count == 0)
            return FileListResult.Failed(WorkflowMultipartChatRequestParseError.InvalidFileInput);

        var files = new List<IFormFile>(form.Files.Count);
        foreach (var file in form.Files)
        {
            if (!string.Equals(file.Name, expectedName, StringComparison.Ordinal))
                return FileListResult.Failed(WorkflowMultipartChatRequestParseError.InvalidFileInput);

            files.Add(file);
        }

        return FileListResult.Success(files);
    }

    private static async ValueTask<byte[]> ReadFileContentAsync(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        await using var source = file.OpenReadStream();
        using var target = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await source.CopyToAsync(target, cancellationToken);
        return target.ToArray();
    }

    private static bool IsMultipartForm(string? contentType) =>
        contentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryResolveInputPartType(string? mediaType, out string type)
    {
        var normalized = Normalize(mediaType);
        type = normalized switch
        {
            { } value when value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "image",
            { } value when value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "audio",
            { } value when value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "video",
            { } => "file",
            _ => string.Empty,
        };

        return type.Length > 0;
    }

    private static bool IsAllowedMediaType(string mediaType, IEnumerable<string> allowedMediaTypes) =>
        allowedMediaTypes.Any(allowed =>
            string.Equals(Normalize(allowed), mediaType, StringComparison.OrdinalIgnoreCase));

    private static PayloadParseResult ParsePayload(
        IFormCollection form,
        string payloadFieldName)
    {
        var payload = ResolveScalar(form, payloadFieldName);
        if (payload == null)
            return PayloadParseResult.Success(null);

        try
        {
            return PayloadParseResult.Success(
                JsonSerializer.Deserialize<ChatInput>(payload, ChatWebSocketProtocol.JsonOptions));
        }
        catch (JsonException)
        {
            return PayloadParseResult.Failed(WorkflowMultipartChatRequestParseError.InvalidRequest);
        }
    }

    private static bool ContainsActorFacingFilePayload(ChatInput input) =>
        input.InputParts?.Any(static part =>
            part.DataBase64 != null ||
            part.InlineFile != null ||
            part.FileRef != null) == true;

    private static IReadOnlyList<string>? ResolveWorkflowYamls(IFormCollection form)
    {
        if (!form.TryGetValue("workflowYamls", out var values) || values.Count == 0)
            return null;

        var normalized = values
            .Select(static value => string.IsNullOrWhiteSpace(value) ? null : value)
            .Where(static value => value != null)
            .Cast<string>()
            .ToArray();

        return normalized.Length == 0 ? null : normalized;
    }

    private static ChatInputFileRef ToInputFileRef(WorkflowFileRef fileRef) =>
        new()
        {
            FileId = fileRef.FileId,
            ArtifactId = fileRef.ArtifactId,
            SourceKind = "form_upload",
            SourceMessageId = fileRef.SourceMessageId,
            SourceResourceKey = fileRef.SourceResourceKey,
            FileName = fileRef.FileName,
            MediaType = fileRef.MediaType,
            CreatedAtUnixMs = fileRef.CreatedAtUnixMs,
            ExpiresAtUnixMs = fileRef.ExpiresAtUnixMs,
            Sha256 = fileRef.Sha256,
            OwnerRunId = fileRef.OwnerRunId,
            OwnerScopeId = fileRef.OwnerScopeId,
        };

    private static string? ResolveScalar(IFormCollection form, string key)
    {
        if (!form.TryGetValue(key, out StringValues values) || values.Count == 0)
            return null;

        var value = values[0];
        return Normalize(value);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct FileListResult(
        IReadOnlyList<IFormFile>? Files,
        WorkflowMultipartChatRequestParseError? Error)
    {
        public bool Succeeded => Error == null && Files is { Count: > 0 };

        public static FileListResult Success(IReadOnlyList<IFormFile> files) => new(files, null);

        public static FileListResult Failed(WorkflowMultipartChatRequestParseError error) => new(null, error);
    }

    private readonly record struct PayloadParseResult(ChatInput? Input, WorkflowMultipartChatRequestParseError? Error)
    {
        public bool Succeeded => Error == null;

        public static PayloadParseResult Success(ChatInput? input) => new(input, null);

        public static PayloadParseResult Failed(WorkflowMultipartChatRequestParseError error) => new(null, error);
    }
}

internal readonly record struct WorkflowMultipartChatRequestParseResult(
    ChatInput? Input,
    WorkflowMultipartChatRequestParseError? Error)
{
    public bool Succeeded => Error == null && Input != null;

    public int StatusCode => Error?.StatusCode ?? StatusCodes.Status200OK;

    public string Code => Error?.Code ?? string.Empty;

    public string Message => Error?.Message ?? string.Empty;

    public static WorkflowMultipartChatRequestParseResult Success(ChatInput input) => new(input, null);

    public static WorkflowMultipartChatRequestParseResult Failed(WorkflowMultipartChatRequestParseError error) =>
        new(null, error);
}

internal readonly record struct WorkflowMultipartChatRequestParseError(
    int StatusCode,
    string Code,
    string Message)
{
    public static readonly WorkflowMultipartChatRequestParseError UnsupportedMediaType = new(
        StatusCodes.Status415UnsupportedMediaType,
        "UNSUPPORTED_MEDIA_TYPE",
        "Content-Type must be multipart/form-data.");

    public static readonly WorkflowMultipartChatRequestParseError InvalidRequest = new(
        StatusCodes.Status400BadRequest,
        "INVALID_CHAT_INPUT",
        "Multipart chat request is invalid.");

    public static readonly WorkflowMultipartChatRequestParseError InvalidFileInput = new(
        StatusCodes.Status400BadRequest,
        "INVALID_FILE_INPUT",
        "Multipart chat file input is invalid.");
}
