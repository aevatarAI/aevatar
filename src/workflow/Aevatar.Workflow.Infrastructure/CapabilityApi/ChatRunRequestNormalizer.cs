using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal readonly record struct ChatRunRequestNormalizationResult(
    WorkflowChatRunRequest? Request,
    WorkflowChatRunStartError Error)
{
    public bool Succeeded => Error == WorkflowChatRunStartError.None && Request != null;

    public static ChatRunRequestNormalizationResult Success(WorkflowChatRunRequest request) =>
        new(request, WorkflowChatRunStartError.None);

    public static ChatRunRequestNormalizationResult Failed(WorkflowChatRunStartError error) =>
        new(null, error);
}

internal static class ChatRunRequestNormalizer
{
    /// <summary>
    /// Maximum allowed size for inline base64 media data per content part (20 MB encoded).
    /// Base64 encoding expands data by ~33%, so this corresponds to roughly 15 MB of raw media.
    /// </summary>
    internal const int MaxDataBase64Length = 20 * 1024 * 1024;

    private readonly record struct NormalizedChatContext(
        IReadOnlyDictionary<string, string> Metadata,
        string? ScopeId,
        WorkflowChatRunStartError Error)
    {
        public bool Succeeded => Error == WorkflowChatRunStartError.None;
    }

    public static ChatRunRequestNormalizationResult Normalize(
        ChatInput input,
        WorkflowCapabilitiesDocument? capabilities = null,
        IReadOnlyDictionary<string, string>? defaultMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        var normalizedAgentId = NormalizeAgentId(input.AgentId);
        var normalizedInputParts = NormalizeInputParts(input.InputParts);
        if (HasOnlyUnsupportedInputParts(input, normalizedInputParts))
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.PromptRequired);

        var normalizedContext = NormalizeContext(input.ScopeId, input.Metadata, defaultMetadata);
        if (!normalizedContext.Succeeded)
            return ChatRunRequestNormalizationResult.Failed(normalizedContext.Error);

        var normalizedMetadata = normalizedContext.Metadata;
        var requestedWorkflowName = NormalizeWorkflowName(input.Workflow);
        var inlineWorkflowYamls = NormalizeInlineWorkflowYamls(input.WorkflowYamls);
        var legacyWorkflowYaml = input.WorkflowYaml;
        var hasLegacyWorkflowYaml = legacyWorkflowYaml != null;

        if (hasLegacyWorkflowYaml && string.IsNullOrWhiteSpace(legacyWorkflowYaml))
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml);

        if (hasLegacyWorkflowYaml && inlineWorkflowYamls.Count > 0)
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml);

        if (hasLegacyWorkflowYaml)
            inlineWorkflowYamls = [legacyWorkflowYaml!];

        var rawPrompt = ResolvePrompt(input.Prompt, normalizedInputParts);
        if (rawPrompt.Length == 0)
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.PromptRequired);

        var normalizedPrompt = WorkflowAuthoringSkillPromptAugmentor.AugmentPrompt(
            rawPrompt,
            requestedWorkflowName,
            inlineWorkflowYamls.Count > 0,
            normalizedMetadata,
            capabilities);

        if (inlineWorkflowYamls.Count > 0)
        {
            return ChatRunRequestNormalizationResult.Success(
                new WorkflowChatRunRequest(
                    Prompt: normalizedPrompt,
                    WorkflowName: string.IsNullOrWhiteSpace(requestedWorkflowName) ? null : requestedWorkflowName,
                    ActorId: normalizedAgentId,
                    SessionId: NormalizeSessionId(input.SessionId),
                    InputParts: normalizedInputParts,
                    WorkflowYamls: inlineWorkflowYamls,
                    Metadata: normalizedMetadata,
                    ScopeId: normalizedContext.ScopeId));
        }

        if (!string.IsNullOrWhiteSpace(requestedWorkflowName))
        {
            return ChatRunRequestNormalizationResult.Success(
                new WorkflowChatRunRequest(
                    Prompt: normalizedPrompt,
                    WorkflowName: requestedWorkflowName,
                    ActorId: normalizedAgentId,
                    SessionId: NormalizeSessionId(input.SessionId),
                    InputParts: normalizedInputParts,
                    WorkflowYamls: null,
                    Metadata: normalizedMetadata,
                    ScopeId: normalizedContext.ScopeId));
        }

        return ChatRunRequestNormalizationResult.Success(
            new WorkflowChatRunRequest(
                Prompt: normalizedPrompt,
                WorkflowName: null,
                ActorId: normalizedAgentId,
                SessionId: NormalizeSessionId(input.SessionId),
                InputParts: normalizedInputParts,
                WorkflowYamls: null,
                Metadata: normalizedMetadata,
                ScopeId: normalizedContext.ScopeId));
    }

    private static IReadOnlyList<string> NormalizeInlineWorkflowYamls(IReadOnlyList<string>? workflowYamls)
    {
        if (workflowYamls == null || workflowYamls.Count == 0)
            return [];

        var normalized = new List<string>(workflowYamls.Count);
        foreach (var yaml in workflowYamls)
            normalized.Add(yaml ?? string.Empty);
        return normalized;
    }

    private static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName) ? string.Empty : workflowName.Trim();

    private static IReadOnlyList<WorkflowChatInputPart>? NormalizeInputParts(IReadOnlyList<ChatInputContentPart>? inputParts)
    {
        if (inputParts == null || inputParts.Count == 0)
            return null;

        var normalized = new List<WorkflowChatInputPart>(inputParts.Count);
        foreach (var part in inputParts)
        {
            if (part == null || string.IsNullOrWhiteSpace(part.Type))
                continue;

            if (!TryParseContentPartKind(part.Type, out var kind))
                continue;

            if (!string.IsNullOrWhiteSpace(part.DataBase64) && part.DataBase64.Length > MaxDataBase64Length)
                continue;

            normalized.Add(new WorkflowChatInputPart
            {
                Kind = kind,
                Text = string.IsNullOrWhiteSpace(part.Text) ? null : part.Text,
                DataBase64 = string.IsNullOrWhiteSpace(part.DataBase64) ? null : part.DataBase64,
                MediaType = string.IsNullOrWhiteSpace(part.MediaType) ? null : part.MediaType,
                Uri = string.IsNullOrWhiteSpace(part.Uri) ? null : part.Uri,
                Name = string.IsNullOrWhiteSpace(part.Name) ? null : part.Name,
            });
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static bool HasOnlyUnsupportedInputParts(
        ChatInput input,
        IReadOnlyList<WorkflowChatInputPart>? normalizedInputParts) =>
        string.IsNullOrWhiteSpace(input.Prompt) &&
        input.InputParts is { Count: > 0 } &&
        normalizedInputParts == null;

    private static NormalizedChatContext NormalizeContext(
        string? explicitScopeId,
        IDictionary<string, string>? metadata,
        IReadOnlyDictionary<string, string>? defaultMetadata)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalizedScopeId = NormalizeScopeId(explicitScopeId);
        if (defaultMetadata is { Count: > 0 })
        {
            foreach (var (key, value) in defaultMetadata)
            {
                if (!AddNormalizedMetadataEntry(normalized, ref normalizedScopeId, key, value))
                    return new NormalizedChatContext(normalized, null, WorkflowChatRunStartError.ConflictingScopeId);
            }
        }

        if (metadata is { Count: > 0 })
        {
            foreach (var (key, value) in metadata)
            {
                if (!AddNormalizedMetadataEntry(normalized, ref normalizedScopeId, key, value))
                    return new NormalizedChatContext(normalized, null, WorkflowChatRunStartError.ConflictingScopeId);
            }
        }

        return new NormalizedChatContext(normalized, normalizedScopeId, WorkflowChatRunStartError.None);
    }

    private static bool AddNormalizedMetadataEntry(
        IDictionary<string, string> metadata,
        ref string? scopeId,
        string key,
        string value)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
        var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
            return true;

        if (IsScopeMetadataKey(normalizedKey))
        {
            return TryAssignScopeId(ref scopeId, normalizedValue);
        }

        metadata[normalizedKey] = normalizedValue;
        return true;
    }

    private static bool IsScopeMetadataKey(string key) =>
        string.Equals(key, "scope_id", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, WorkflowRunCommandMetadataKeys.ScopeId, StringComparison.OrdinalIgnoreCase);

    private static bool TryAssignScopeId(ref string? currentScopeId, string? candidateScopeId)
    {
        var normalizedCandidate = NormalizeScopeId(candidateScopeId);
        if (normalizedCandidate == null)
            return true;

        if (string.IsNullOrWhiteSpace(currentScopeId))
        {
            currentScopeId = normalizedCandidate;
            return true;
        }

        return string.Equals(currentScopeId, normalizedCandidate, StringComparison.Ordinal);
    }

    private static string? NormalizeScopeId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId) ? null : scopeId.Trim();

    private static string? NormalizeAgentId(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();

    private static string? NormalizeSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();

    private static string ResolvePrompt(string? prompt, IReadOnlyList<WorkflowChatInputPart>? inputParts)
    {
        if (!string.IsNullOrWhiteSpace(prompt))
            return prompt.Trim();

        if (inputParts == null || inputParts.Count == 0)
            return string.Empty;

        var textParts = inputParts
            .Where(part => part.Kind == WorkflowChatInputPartKind.Text && !string.IsNullOrWhiteSpace(part.Text))
            .Select(part => part.Text!.Trim())
            .ToArray();

        if (textParts.Length > 0)
            return string.Join("\n", textParts);

        return string.Join(
            ", ",
            inputParts.Select(part => part.Kind switch
            {
                WorkflowChatInputPartKind.Image => "[image]",
                WorkflowChatInputPartKind.Audio => "[audio]",
                WorkflowChatInputPartKind.Video => "[video]",
                WorkflowChatInputPartKind.Pdf => "[pdf]",
                _ => "[content]",
            }));
    }

    private static bool TryParseContentPartKind(string raw, out WorkflowChatInputPartKind kind)
    {
        kind = raw.Trim().ToLowerInvariant() switch
        {
            "text" => WorkflowChatInputPartKind.Text,
            "image" => WorkflowChatInputPartKind.Image,
            "audio" => WorkflowChatInputPartKind.Audio,
            "video" => WorkflowChatInputPartKind.Video,
            "pdf" => WorkflowChatInputPartKind.Pdf,
            _ => WorkflowChatInputPartKind.Unspecified,
        };

        return kind != WorkflowChatInputPartKind.Unspecified;
    }
}
