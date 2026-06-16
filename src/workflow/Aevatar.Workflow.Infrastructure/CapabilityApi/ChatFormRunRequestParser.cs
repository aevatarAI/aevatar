using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class ChatFormRunRequestParser
{
    public ChatFormRunRequestParserResult Parse(
        IFormCollection form,
        ChatInputFileRef uploadedFileRef,
        string inputPartType,
        string payloadFieldName)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(uploadedFileRef);

        var payloadResult = ParsePayload(form, payloadFieldName);
        if (!payloadResult.Succeeded)
            return ChatFormRunRequestParserResult.Failed(payloadResult.Error!.Value);

        var source = payloadResult.Input ?? new ChatInput();
        if (ContainsActorFacingFilePayload(source))
            return ChatFormRunRequestParserResult.Failed(WorkflowMultipartChatRequestParseError.InvalidFileInput);

        var inputParts = new List<ChatInputContentPart>(source.InputParts ?? []);
        inputParts.Add(new ChatInputContentPart
        {
            Type = inputPartType,
            MediaType = uploadedFileRef.MediaType,
            Uri = uploadedFileRef.ArtifactId ?? uploadedFileRef.Uri,
            Name = uploadedFileRef.FileName ?? uploadedFileRef.Name,
            FileRef = uploadedFileRef,
        });

        return ChatFormRunRequestParserResult.Success(source with
        {
            Prompt = ResolveScalar(form, "prompt") ?? source.Prompt,
            Workflow = ResolveScalar(form, "workflow") ?? source.Workflow,
            SessionId = ResolveScalar(form, "sessionId") ?? source.SessionId,
            ScopeId = ResolveScalar(form, "scopeId") ?? source.ScopeId,
            WorkflowYaml = ResolveScalar(form, "workflowYaml") ?? source.WorkflowYaml,
            WorkflowYamls = ResolveWorkflowYamls(form) ?? source.WorkflowYamls,
            InputParts = inputParts,
        });
    }

    public WorkflowMultipartChatRequestParseError? ValidatePayload(
        IFormCollection form,
        string payloadFieldName)
    {
        ArgumentNullException.ThrowIfNull(form);

        var payloadResult = ParsePayload(form, payloadFieldName);
        if (!payloadResult.Succeeded)
            return payloadResult.Error!.Value;

        return payloadResult.Input != null && ContainsActorFacingFilePayload(payloadResult.Input)
            ? WorkflowMultipartChatRequestParseError.InvalidFileInput
            : null;
    }

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

    private static string? ResolveScalar(IFormCollection form, string key)
    {
        if (!form.TryGetValue(key, out StringValues values) || values.Count == 0)
            return null;

        var value = values[0];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private readonly record struct PayloadParseResult(ChatInput? Input, WorkflowMultipartChatRequestParseError? Error)
    {
        public bool Succeeded => Error == null;

        public static PayloadParseResult Success(ChatInput? input) => new(input, null);

        public static PayloadParseResult Failed(WorkflowMultipartChatRequestParseError error) => new(null, error);
    }
}

internal readonly record struct ChatFormRunRequestParserResult(
    ChatInput? Input,
    WorkflowMultipartChatRequestParseError? Error)
{
    public bool Succeeded => Error == null && Input != null;

    public static ChatFormRunRequestParserResult Success(ChatInput input) => new(input, null);

    public static ChatFormRunRequestParserResult Failed(WorkflowMultipartChatRequestParseError error) => new(null, error);
}
