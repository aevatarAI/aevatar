using System.Text.Json;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class WorkflowMultipartChatRequestParser
{
    private readonly WorkflowMultipartFileInputParser _fileInputParser;
    private readonly IFileArtifactIngressPort _fileIngressPort;

    public WorkflowMultipartChatRequestParser(
        WorkflowMultipartFileInputParser fileInputParser,
        IFileArtifactIngressPort fileIngressPort)
    {
        _fileInputParser = fileInputParser;
        _fileIngressPort = fileIngressPort;
    }

    internal WorkflowMultipartChatRequestParser(
        IFileArtifactIngressPort fileIngressPort,
        Microsoft.Extensions.Options.IOptions<WorkflowMultipartFileIngressOptions> multipartOptions,
        Microsoft.Extensions.Options.IOptions<WorkflowFormFileIngressOptions>? formOptions = null)
        : this(new WorkflowMultipartFileInputParser(multipartOptions, formOptions), fileIngressPort)
    {
    }

    public ValueTask<WorkflowMultipartChatRequestParseResult> ParseAsync(
        HttpContext http,
        CancellationToken cancellationToken = default) =>
        ParseAsync(http, ownerScopeId: null, cancellationToken);

    public async ValueTask<WorkflowMultipartChatRequestParseResult> ParseAsync(
        HttpContext http,
        string? ownerScopeId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        var multipartResult = await _fileInputParser.ParseAsync(http, cancellationToken);
        if (!multipartResult.Succeeded)
            return WorkflowMultipartChatRequestParseResult.Failed(ToChatError(multipartResult.Error!.Value));

        var form = multipartResult.Form!;
        if (!form.HasFiles)
            return WorkflowMultipartChatRequestParseResult.Failed(
                WorkflowMultipartChatRequestParseError.InvalidFileInput);

        var payloadResult = ParsePayload(form.RawPayloadJson);
        if (!payloadResult.Succeeded)
            return WorkflowMultipartChatRequestParseResult.Failed(payloadResult.Error!.Value);

        var source = payloadResult.Input ?? new HttpChatInput();
        if (ContainsActorFacingFilePayload(source))
            return WorkflowMultipartChatRequestParseResult.Failed(WorkflowMultipartChatRequestParseError.InvalidFileInput);

        if (form.ResolveScalar("scopeId") != null)
            return WorkflowMultipartChatRequestParseResult.Failed(WorkflowMultipartChatRequestParseError.InvalidRequest);

        var normalizedOwnerScopeId = string.IsNullOrWhiteSpace(ownerScopeId)
            ? null
            : ownerScopeId.Trim();
        var inputParts = new List<ChatInputContentPart>(source.InputParts ?? []);
        foreach (var file in form.PendingFiles)
        {
            FileArtifactIngressResult ingressResult;
            try
            {
                ingressResult = await _fileIngressPort.IngestAsync(
                    WorkflowMultipartFileInputParser.BuildIngressRequest(file, normalizedOwnerScopeId),
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

            inputParts.Add(WorkflowMultipartFileInputParser.BuildInputPart(file, ingressResult.FileRef));
        }

        return WorkflowMultipartChatRequestParseResult.Success(source with
        {
            Prompt = form.ResolveScalar("prompt") ?? source.Prompt,
            Workflow = form.ResolveScalar("workflow") ?? source.Workflow,
            SessionId = form.ResolveScalar("sessionId") ?? source.SessionId,
            WorkflowYaml = form.ResolveScalar("workflowYaml") ?? source.WorkflowYaml,
            WorkflowYamls = form.ResolveRepeatedScalars("workflowYamls") ?? source.WorkflowYamls,
            InputParts = inputParts,
        });
    }

    private static PayloadParseResult ParsePayload(string? payload)
    {
        if (payload == null)
            return PayloadParseResult.Success(null);

        try
        {
            return PayloadParseResult.Success(
                JsonSerializer.Deserialize<HttpChatInput>(payload, ChatWebSocketProtocol.JsonOptions));
        }
        catch (JsonException)
        {
            return PayloadParseResult.Failed(WorkflowMultipartChatRequestParseError.InvalidRequest);
        }
    }

    private static bool ContainsActorFacingFilePayload(HttpChatInput input) =>
        input.InputParts?.Any(static part =>
            part.DataBase64 != null ||
            part.InlineFile != null ||
            part.FileRef != null) == true;

    private static WorkflowMultipartChatRequestParseError ToChatError(WorkflowMultipartFileInputParseError error) =>
        new(error.StatusCode, error.Code, error.Message);

    private readonly record struct PayloadParseResult(HttpChatInput? Input, WorkflowMultipartChatRequestParseError? Error)
    {
        public bool Succeeded => Error == null;

        public static PayloadParseResult Success(HttpChatInput? input) => new(input, null);

        public static PayloadParseResult Failed(WorkflowMultipartChatRequestParseError error) => new(null, error);
    }
}

internal readonly record struct WorkflowMultipartChatRequestParseResult(
    HttpChatInput? Input,
    WorkflowMultipartChatRequestParseError? Error)
{
    public bool Succeeded => Error == null && Input != null;

    public int StatusCode => Error?.StatusCode ?? StatusCodes.Status200OK;

    public string Code => Error?.Code ?? string.Empty;

    public string Message => Error?.Message ?? string.Empty;

    public static WorkflowMultipartChatRequestParseResult Success(HttpChatInput input) => new(input, null);

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
