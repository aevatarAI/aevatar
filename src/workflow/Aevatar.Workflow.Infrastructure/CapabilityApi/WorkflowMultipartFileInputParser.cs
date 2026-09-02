using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public sealed class WorkflowMultipartFileInputParser
{
    private readonly IOptions<WorkflowMultipartFileIngressOptions> _multipartOptions;
    private readonly IOptions<WorkflowFormFileIngressOptions> _formOptions;

    public WorkflowMultipartFileInputParser(
        IOptions<WorkflowMultipartFileIngressOptions> multipartOptions,
        IOptions<WorkflowFormFileIngressOptions>? formOptions = null)
    {
        _multipartOptions = multipartOptions;
        _formOptions = formOptions ?? Options.Create(new WorkflowFormFileIngressOptions());
    }

    public async ValueTask<WorkflowMultipartFileInputParseResult> ParseAsync(
        HttpContext http,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!IsMultipartForm(http.Request.ContentType))
            return WorkflowMultipartFileInputParseResult.Failed(
                WorkflowMultipartFileInputParseError.UnsupportedMediaType);

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return WorkflowMultipartFileInputParseResult.Failed(
                WorkflowMultipartFileInputParseError.InvalidRequest);
        }

        var filesResult = await ResolveFilesAsync(form, _formOptions.Value.FileFieldName, cancellationToken);
        if (!filesResult.Succeeded)
            return WorkflowMultipartFileInputParseResult.Failed(filesResult.Error!.Value);

        var fields = ResolveFields(form);
        return WorkflowMultipartFileInputParseResult.Success(new WorkflowMultipartFileInputForm(
            fields,
            filesResult.Files!,
            ResolveScalar(fields, _formOptions.Value.PayloadFieldName)));
    }

    private async ValueTask<FileListResult> ResolveFilesAsync(
        IFormCollection form,
        string fileFieldName,
        CancellationToken cancellationToken)
    {
        var expectedName = Normalize(fileFieldName) ?? "file";
        var files = new List<WorkflowPendingMultipartFile>(form.Files.Count);
        foreach (var file in form.Files)
        {
            if (!string.Equals(file.Name, expectedName, StringComparison.Ordinal))
                return FileListResult.Failed(WorkflowMultipartFileInputParseError.InvalidFileInput);

            var validationError = ValidateFile(file);
            if (validationError != null)
                return FileListResult.Failed(validationError.Value);

            var content = await ReadFileContentAsync(file, cancellationToken);
            if (content.Length == 0)
                return FileListResult.Failed(WorkflowMultipartFileInputParseError.InvalidFileInput);

            files.Add(new WorkflowPendingMultipartFile(
                content,
                Normalize(file.FileName),
                Normalize(file.ContentType),
                ResolveInputPartType(file.ContentType)));
        }

        return FileListResult.Success(files);
    }

    private WorkflowMultipartFileInputParseError? ValidateFile(IFormFile file)
    {
        if (file.Length <= 0)
            return WorkflowMultipartFileInputParseError.InvalidFileInput;

        var options = _multipartOptions.Value;
        if (options.MaxFileBytes <= 0 || file.Length > options.MaxFileBytes)
            return WorkflowMultipartFileInputParseError.InvalidFileInput;

        var mediaType = Normalize(file.ContentType);
        if (mediaType == null || !IsAllowedMediaType(mediaType, options.AllowedMediaTypes))
            return WorkflowMultipartFileInputParseError.InvalidFileInput;

        return null;
    }

    private static Dictionary<string, StringValues> ResolveFields(IFormCollection form)
    {
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        foreach (var (key, value) in form)
        {
            fields[key] = value;
        }

        return fields;
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

    private static string ResolveInputPartType(string? mediaType)
    {
        var normalized = Normalize(mediaType);
        return normalized switch
        {
            { } value when value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "image",
            { } value when value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "audio",
            { } value when value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "video",
            _ => "file",
        };
    }

    private static bool IsAllowedMediaType(string mediaType, IEnumerable<string> allowedMediaTypes) =>
        allowedMediaTypes.Any(allowed =>
            string.Equals(Normalize(allowed), mediaType, StringComparison.OrdinalIgnoreCase));

    public static string? ResolveScalar(IReadOnlyDictionary<string, StringValues> form, string key)
    {
        if (!form.TryGetValue(key, out var values) || values.Count == 0)
            return null;

        return Normalize(values[0]);
    }

    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsMultipartForm(string? contentType) =>
        contentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true;

    public static FileArtifactIngressRequest BuildIngressRequest(
        WorkflowPendingMultipartFile file,
        string? ownerScopeId) =>
        new(
            file.Content,
            FileArtifactSourceKind.FormUpload,
            FileName: file.FileName,
            MediaType: file.MediaType,
            OwnerScopeId: Normalize(ownerScopeId));

    public static ChatInputContentPart BuildInputPart(
        WorkflowPendingMultipartFile file,
        FileArtifactRef fileRef)
    {
        var inputFileRef = ToInputFileRef(fileRef);
        return new ChatInputContentPart
        {
            Type = file.InputPartType,
            MediaType = inputFileRef.MediaType,
            Uri = inputFileRef.ArtifactId ?? inputFileRef.Uri,
            Name = inputFileRef.FileName ?? inputFileRef.Name,
            FileRef = inputFileRef,
        };
    }

    private static ChatInputFileRef ToInputFileRef(FileArtifactRef fileRef) =>
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

    private readonly record struct FileListResult(
        IReadOnlyList<WorkflowPendingMultipartFile>? Files,
        WorkflowMultipartFileInputParseError? Error)
    {
        public bool Succeeded => Error == null && Files != null;

        public static FileListResult Success(IReadOnlyList<WorkflowPendingMultipartFile> files) => new(files, null);

        public static FileListResult Failed(WorkflowMultipartFileInputParseError error) => new(null, error);
    }
}

public sealed record WorkflowMultipartFileInputForm(
    IReadOnlyDictionary<string, StringValues> Fields,
    IReadOnlyList<WorkflowPendingMultipartFile> PendingFiles,
    string? RawPayloadJson)
{
    public bool HasFiles => PendingFiles.Count > 0;

    public string? ResolveScalar(string key) =>
        WorkflowMultipartFileInputParser.ResolveScalar(Fields, key);

    public IReadOnlyList<string>? ResolveRepeatedScalars(string key)
    {
        if (!Fields.TryGetValue(key, out var values) || values.Count == 0)
            return null;

        var normalized = values
            .Select(static value => string.IsNullOrWhiteSpace(value) ? null : value)
            .Where(static value => value != null)
            .Cast<string>()
            .ToArray();

        return normalized.Length == 0 ? null : normalized;
    }
}

public sealed record WorkflowPendingMultipartFile(
    ReadOnlyMemory<byte> Content,
    string? FileName,
    string? MediaType,
    string InputPartType);

public readonly record struct WorkflowMultipartFileInputParseResult(
    WorkflowMultipartFileInputForm? Form,
    WorkflowMultipartFileInputParseError? Error)
{
    public bool Succeeded => Error == null && Form != null;

    public bool HasFiles => Form?.HasFiles == true;

    public string? RawPayloadJson => Form?.RawPayloadJson;

    public int StatusCode => Error?.StatusCode ?? StatusCodes.Status200OK;

    public string Code => Error?.Code ?? string.Empty;

    public string Message => Error?.Message ?? string.Empty;

    public static WorkflowMultipartFileInputParseResult Success(WorkflowMultipartFileInputForm form) =>
        new(form, null);

    public static WorkflowMultipartFileInputParseResult Failed(WorkflowMultipartFileInputParseError error) =>
        new(null, error);
}

public readonly record struct WorkflowMultipartFileInputParseError(
    int StatusCode,
    string Code,
    string Message)
{
    public static readonly WorkflowMultipartFileInputParseError UnsupportedMediaType = new(
        StatusCodes.Status415UnsupportedMediaType,
        "UNSUPPORTED_MEDIA_TYPE",
        "Content-Type must be multipart/form-data.");

    public static readonly WorkflowMultipartFileInputParseError InvalidRequest = new(
        StatusCodes.Status400BadRequest,
        "INVALID_CHAT_INPUT",
        "Multipart chat request is invalid.");

    public static readonly WorkflowMultipartFileInputParseError InvalidFileInput = new(
        StatusCodes.Status400BadRequest,
        "INVALID_FILE_INPUT",
        "Multipart chat file input is invalid.");
}
