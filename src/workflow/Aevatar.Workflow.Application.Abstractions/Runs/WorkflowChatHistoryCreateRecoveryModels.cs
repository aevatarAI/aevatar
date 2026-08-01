using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public enum WorkflowChatHistoryCreateRecoveryStatus
{
    NotFound = 0,
    Reserved = 1,
    Bound = 2,
    AppendDispatched = 3,
    Abandoned = 4,
    Failed = 5,
    AppendCommitted = 6,
    AppendRejected = 7,
    TerminalReconciliationPrepared = 8,
}

public sealed record WorkflowChatHistoryCreateRecovery(
    WorkflowChatHistoryCreateRecoveryStatus Status,
    string ScopeId,
    string CommandId,
    string? ConversationId,
    string? TurnId,
    string? WorkflowActorId,
    string? WorkflowCommandId,
    string? WorkflowCorrelationId,
    string? RequestFingerprint,
    long StateVersion,
    DateTimeOffset UpdatedAt);

public interface IWorkflowChatHistoryCreateRecoveryReadPort
{
    Task<WorkflowChatHistoryCreateRecovery?> GetAsync(
        string scopeId,
        string commandId,
        CancellationToken ct = default);
}

public static class WorkflowChatHistoryCreateRecoveryIds
{
    public static string FromScopeAndCommandId(string scopeId, string commandId) =>
        $"chat-history-create:{HashTuple(Normalize(scopeId), Normalize(commandId))}";

    private static string HashTuple(params string[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetByteCount(part);
            builder.Append(bytes);
            builder.Append(':');
            builder.Append(part);
            builder.Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

public static class WorkflowChatCreateRequestFingerprint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public static string Compute(WorkflowChatRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var material = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["conversation"] = NormalizeConversation(request.ChatConversation),
            ["inputParts"] = request.InputParts?.Select(NormalizeInputPart).ToArray(),
            ["llmControl"] = NormalizeLlmControl(request.LlmControl),
            ["metadata"] = NormalizeDictionary(request.Metadata),
            ["prompt"] = NormalizeString(request.Prompt),
            ["scopeId"] = NormalizeString(request.ScopeId),
            ["sessionId"] = NormalizeString(request.SessionId),
            ["source"] = NormalizeSource(request.Source),
        };

        var json = JsonSerializer.Serialize(material, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static object NormalizeConversation(WorkflowChatConversationIntent? conversation) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["conversationId"] = NormalizeString(conversation?.ConversationId),
            ["intent"] = (conversation?.Intent ?? WorkflowChatConversationIntentKind.None).ToString(),
        };

    private static object NormalizeSource(WorkflowChatSource source) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["catalogName"] = source.CatalogName == null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["workflowName"] = NormalizeString(source.CatalogName.WorkflowName),
                },
            ["definitionActor"] = source.DefinitionActorSource == null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["actorId"] = NormalizeString(source.DefinitionActorSource.ActorId),
                    ["workflowName"] = NormalizeString(source.DefinitionActorSource.WorkflowName),
                },
            ["inlineBundle"] = source.InlineBundle == null
                ? null
                : new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["actorId"] = NormalizeString(source.InlineBundle.ActorId),
                    ["entryName"] = NormalizeString(source.InlineBundle.EntryName),
                    ["yamlDocuments"] = source.InlineBundle.YamlDocuments
                        .Select(static document => new SortedDictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["name"] = NormalizeString(document.Name),
                            ["yaml"] = document.Yaml ?? string.Empty,
                        })
                        .ToArray(),
                },
            ["kind"] = source.Kind.ToString(),
        };

    private static object NormalizeInputPart(WorkflowChatInputPart part) =>
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dataBase64"] = part.DataBase64 ?? string.Empty,
            ["fileRef"] = NormalizeFileRef(part.FileRef),
            ["kind"] = part.Kind.ToString(),
            ["mediaType"] = NormalizeString(part.MediaType),
            ["name"] = NormalizeString(part.Name),
            ["text"] = part.Text ?? string.Empty,
            ["uri"] = NormalizeString(part.Uri),
        };

    private static object? NormalizeFileRef(FileArtifactRef? fileRef)
    {
        if (fileRef == null)
            return null;

        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["artifactId"] = NormalizeString(fileRef.ArtifactId),
            ["createdAtUnixMs"] = fileRef.CreatedAtUnixMs,
            ["expiresAtUnixMs"] = fileRef.ExpiresAtUnixMs,
            ["fileId"] = NormalizeString(fileRef.FileId),
            ["fileName"] = NormalizeString(fileRef.FileName),
            ["mediaType"] = NormalizeString(fileRef.MediaType),
            ["ownerRunId"] = NormalizeString(fileRef.OwnerRunId),
            ["ownerScopeId"] = NormalizeString(fileRef.OwnerScopeId),
            ["sha256"] = NormalizeString(fileRef.Sha256),
            ["sizeBytes"] = fileRef.SizeBytes,
            ["sourceKind"] = fileRef.SourceKind.ToString(),
            ["sourceMessageId"] = NormalizeString(fileRef.SourceMessageId),
            ["sourceResourceKey"] = NormalizeString(fileRef.SourceResourceKey),
        };
    }

    private static object? NormalizeLlmControl(WorkflowLlmControl? control)
    {
        if (control == null)
            return null;

        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["maxToolRoundsOverride"] = control.MaxToolRoundsOverride,
            ["modelOverride"] = NormalizeString(control.ModelOverride),
            ["routePreference"] = NormalizeString(control.RoutePreference),
            ["senderNyxIdAccessToken"] = NormalizeString(control.SenderNyxIdAccessToken),
            ["userMemoryPrompt"] = NormalizeString(control.UserMemoryPrompt),
        };
    }

    private static object? NormalizeDictionary(IReadOnlyDictionary<string, string>? source)
    {
        if (source is not { Count: > 0 })
            return null;

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            var key = NormalizeString(item.Key);
            if (key.Length == 0)
                continue;
            result[key] = NormalizeString(item.Value);
        }

        return result.Count == 0 ? null : result;
    }

    private static string NormalizeString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
