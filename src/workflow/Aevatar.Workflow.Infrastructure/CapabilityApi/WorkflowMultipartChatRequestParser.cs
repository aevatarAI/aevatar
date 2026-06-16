using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class WorkflowMultipartChatRequestParser
{
    private readonly IWorkflowFileIngressPort _fileIngressPort;
    private readonly ChatFormRunRequestParser _formParser;
    private readonly IOptions<WorkflowMultipartFileIngressOptions> _multipartOptions;
    private readonly IOptions<WorkflowFormFileIngressOptions> _formOptions;

    public WorkflowMultipartChatRequestParser(
        IWorkflowFileIngressPort fileIngressPort,
        ChatFormRunRequestParser formParser,
        IOptions<WorkflowMultipartFileIngressOptions> multipartOptions,
        IOptions<WorkflowFormFileIngressOptions>? formOptions = null)
    {
        _fileIngressPort = fileIngressPort;
        _formParser = formParser;
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
        var fileResult = ResolveSingleFile(form, formOptions.FileFieldName);
        if (!fileResult.Succeeded)
            return WorkflowMultipartChatRequestParseResult.Failed(fileResult.Error!.Value);

        var file = fileResult.File!;
        if (!TryResolveInputPartType(file.ContentType, out var inputPartType))
            return WorkflowMultipartChatRequestParseResult.Failed(
                WorkflowMultipartChatRequestParseError.InvalidFileInput);

        var validationError = ValidateFile(file);
        if (validationError != null)
            return WorkflowMultipartChatRequestParseResult.Failed(validationError.Value);

        validationError = _formParser.ValidatePayload(form, formOptions.PayloadFieldName);
        if (validationError != null)
            return WorkflowMultipartChatRequestParseResult.Failed(validationError.Value);

        var content = await ReadFileContentAsync(file, cancellationToken);
        if (content.Length == 0)
            return WorkflowMultipartChatRequestParseResult.Failed(
                WorkflowMultipartChatRequestParseError.InvalidFileInput);

        var ownerScopeId = ResolveScalar(form, "scopeId");
        var ingressResult = await _fileIngressPort.IngestAsync(
            new WorkflowFileIngressRequest(
                content,
                WorkflowFileSourceKind.FormUpload,
                FileName: Normalize(file.FileName),
                MediaType: Normalize(file.ContentType),
                OwnerScopeId: ownerScopeId),
            cancellationToken);

        var parsed = _formParser.Parse(
            form,
            ToInputFileRef(ingressResult.FileRef),
            inputPartType,
            formOptions.PayloadFieldName);

        return parsed.Succeeded
            ? WorkflowMultipartChatRequestParseResult.Success(parsed.Input!)
            : WorkflowMultipartChatRequestParseResult.Failed(parsed.Error!.Value);
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

    private static SingleFileResult ResolveSingleFile(IFormCollection form, string fileFieldName)
    {
        var expectedName = Normalize(fileFieldName) ?? "file";
        if (form.Files.Count != 1)
            return SingleFileResult.Failed(WorkflowMultipartChatRequestParseError.InvalidFileInput);

        var file = form.Files[0];
        return string.Equals(file.Name, expectedName, StringComparison.Ordinal)
            ? SingleFileResult.Success(file)
            : SingleFileResult.Failed(WorkflowMultipartChatRequestParseError.InvalidFileInput);
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
            _ => string.Empty,
        };

        return type.Length > 0;
    }

    private static bool IsAllowedMediaType(string mediaType, IEnumerable<string> allowedMediaTypes) =>
        allowedMediaTypes.Any(allowed =>
            string.Equals(Normalize(allowed), mediaType, StringComparison.OrdinalIgnoreCase));

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
        if (!form.TryGetValue(key, out var values) || values.Count == 0)
            return null;

        var value = values[0];
        return Normalize(value);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct SingleFileResult(IFormFile? File, WorkflowMultipartChatRequestParseError? Error)
    {
        public bool Succeeded => Error == null && File != null;

        public static SingleFileResult Success(IFormFile file) => new(file, null);

        public static SingleFileResult Failed(WorkflowMultipartChatRequestParseError error) => new(null, error);
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
