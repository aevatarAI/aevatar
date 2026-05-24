using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using Aevatar.AI.Abstractions.LLMProviders;

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
    // Refactor (iter15/cluster-029):
    //   Old pattern: normalized context carried metadata-derived scope conflict state.
    //   New principle: scope is owned by the typed field; metadata only carries open extension entries.
    private readonly record struct NormalizedChatContext(
        IReadOnlyDictionary<string, string> Metadata,
        string? ScopeId);

    public static ChatRunRequestNormalizationResult Normalize(
        ChatInput input,
        WorkflowCapabilitiesDocument? capabilities = null,
        IReadOnlyDictionary<string, string>? defaultMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        var normalizedInputParts = NormalizeInputParts(input.InputParts);
        if (HasOnlyUnsupportedInputParts(input, normalizedInputParts))
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.PromptRequired);

        var normalizedContext = NormalizeContext(input.ScopeId, input.Metadata, defaultMetadata);
        var normalizedMetadata = normalizedContext.Metadata;
        var sourceResult = NormalizeSource(input);
        if (!sourceResult.Succeeded)
            return ChatRunRequestNormalizationResult.Failed(sourceResult.Error);

        var source = sourceResult.Source!;
        var requestedWorkflowName = source.WorkflowName ?? string.Empty;
        var inlineWorkflowYamls = source.WorkflowYamls ?? [];

        var rawPrompt = ResolvePrompt(input.Prompt, normalizedInputParts);
        if (rawPrompt.Length == 0)
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.PromptRequired);

        var normalizedPrompt = WorkflowAuthoringSkillPromptAugmentor.AugmentPrompt(
            rawPrompt,
            requestedWorkflowName,
            inlineWorkflowYamls.Count > 0,
            normalizedMetadata,
            capabilities);

        return ChatRunRequestNormalizationResult.Success(
            new WorkflowChatRunRequest(
                Prompt: normalizedPrompt,
                WorkflowName: string.IsNullOrWhiteSpace(source.WorkflowName) ? null : source.WorkflowName,
                ActorId: NormalizeAgentId(source.ActorId),
                SessionId: NormalizeSessionId(input.SessionId),
                InputParts: normalizedInputParts,
                WorkflowYamls: source.WorkflowYamls,
                Metadata: normalizedMetadata,
                ScopeId: normalizedContext.ScopeId,
                Source: source,
                LlmControl: NormalizeLlmControl(input.LlmControl)));
    }

    private static LLMControlContext? NormalizeLlmControl(ChatLlmControlInput? source)
    {
        if (source == null)
            return null;

        return new LLMControlContext(
            NormalizeOptional(source.NyxIdAccessToken),
            NormalizeOptional(source.NyxIdOrgToken),
            NormalizeOptional(source.SenderNyxIdAccessToken),
            NormalizeOptional(source.ModelOverride),
            NormalizeOptional(source.NyxIdRoutePreference),
            source.MaxToolRoundsOverride is > 0 ? source.MaxToolRoundsOverride : null,
            NormalizeOptional(source.UserMemoryPrompt));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct SourceNormalizationResult(
        WorkflowChatSource? Source,
        WorkflowChatRunStartError Error)
    {
        public bool Succeeded => Error == WorkflowChatRunStartError.None && Source != null;
        public static SourceNormalizationResult Success(WorkflowChatSource source) =>
            new(source, WorkflowChatRunStartError.None);
        public static SourceNormalizationResult Failed(WorkflowChatRunStartError error) =>
            new(null, error);
    }

    private static SourceNormalizationResult NormalizeSource(ChatInput input)
    {
        if (input.Source != null)
            return NormalizeTypedSource(input.Source);

        var requestedWorkflowName = NormalizeWorkflowName(input.Workflow);
        var normalizedAgentId = NormalizeAgentId(input.AgentId);
        var inlineWorkflowYamls = NormalizeInlineWorkflowYamls(input.WorkflowYamls);
        var legacyWorkflowYaml = input.WorkflowYaml;
        var hasLegacyWorkflowYaml = legacyWorkflowYaml != null;

        if (hasLegacyWorkflowYaml && string.IsNullOrWhiteSpace(legacyWorkflowYaml))
            return SourceNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml);

        if (hasLegacyWorkflowYaml && inlineWorkflowYamls.Count > 0)
            return SourceNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml);

        if (hasLegacyWorkflowYaml)
            inlineWorkflowYamls = [legacyWorkflowYaml!];

        if (inlineWorkflowYamls.Count > 0)
            return SourceNormalizationResult.Success(WorkflowChatSource.InlineYamlBundle(
                inlineWorkflowYamls,
                string.IsNullOrWhiteSpace(requestedWorkflowName) ? null : requestedWorkflowName,
                string.IsNullOrWhiteSpace(normalizedAgentId) ? null : normalizedAgentId));

        if (!string.IsNullOrWhiteSpace(requestedWorkflowName))
            return SourceNormalizationResult.Success(WorkflowChatSource.CatalogWorkflow(requestedWorkflowName));

        if (!string.IsNullOrWhiteSpace(normalizedAgentId))
            return SourceNormalizationResult.Success(WorkflowChatSource.DefinitionActor(normalizedAgentId));

        return SourceNormalizationResult.Success(WorkflowChatSource.Direct());
    }

    private static SourceNormalizationResult NormalizeTypedSource(WorkflowChatSourceInput source)
    {
        var kind = NormalizeSourceKind(source.Kind);
        var workflowName = NormalizeWorkflowName(source.WorkflowName);
        var actorId = NormalizeAgentId(source.ActorId);
        var workflowYamls = NormalizeInlineWorkflowYamls(source.WorkflowYamls);

        return kind switch
        {
            WorkflowChatSourceKind.CatalogWorkflow when string.IsNullOrWhiteSpace(workflowName) =>
                SourceNormalizationResult.Failed(WorkflowChatRunStartError.WorkflowNotFound),
            WorkflowChatSourceKind.CatalogWorkflow =>
                SourceNormalizationResult.Success(WorkflowChatSource.CatalogWorkflow(workflowName)),
            WorkflowChatSourceKind.DefinitionActor when string.IsNullOrWhiteSpace(actorId) =>
                SourceNormalizationResult.Failed(WorkflowChatRunStartError.AgentNotFound),
            WorkflowChatSourceKind.DefinitionActor =>
                SourceNormalizationResult.Success(WorkflowChatSource.DefinitionActor(actorId, string.IsNullOrWhiteSpace(workflowName) ? null : workflowName)),
            WorkflowChatSourceKind.InlineYamlBundle when workflowYamls.Count == 0 || workflowYamls.Any(string.IsNullOrWhiteSpace) =>
                SourceNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml),
            WorkflowChatSourceKind.InlineYamlBundle =>
                SourceNormalizationResult.Success(WorkflowChatSource.InlineYamlBundle(workflowYamls, string.IsNullOrWhiteSpace(workflowName) ? null : workflowName)),
            WorkflowChatSourceKind.Direct =>
                SourceNormalizationResult.Success(WorkflowChatSource.Direct(actorId)),
            _ => SourceNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml),
        };
    }

    private static WorkflowChatSourceKind NormalizeSourceKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "catalog_workflow" or "catalog-workflow" or "catalog" or "workflow" =>
                WorkflowChatSourceKind.CatalogWorkflow,
            "definition_actor" or "definition-actor" or "actor" =>
                WorkflowChatSourceKind.DefinitionActor,
            "inline_yaml_bundle" or "inline-yaml-bundle" or "inline_yaml" or "inline-yaml" =>
                WorkflowChatSourceKind.InlineYamlBundle,
            "direct" => WorkflowChatSourceKind.Direct,
            _ => WorkflowChatSourceKind.Unspecified,
        };

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

    // Refactor (iter15/cluster-029):
    //   Old pattern: scope id / channel facts fell back to metadata bag string keys.
    //   New principle: stable business semantics use typed proto field; metadata bag only for genuine open extension.
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
                AddNormalizedMetadataEntry(normalized, key, value);
        }

        if (metadata is { Count: > 0 })
        {
            foreach (var (key, value) in metadata)
                AddNormalizedMetadataEntry(normalized, key, value);
        }

        return new NormalizedChatContext(normalized, normalizedScopeId);
    }

    // Refactor (iter15/cluster-029):
    //   Old pattern: scope metadata keys promoted into control-flow ScopeId or conflict errors.
    //   New principle: scope metadata keys are not control input and are not forwarded as extension metadata.
    private static void AddNormalizedMetadataEntry(
        IDictionary<string, string> metadata,
        string key,
        string value)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
        var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
            return;

        if (IsScopeMetadataKey(normalizedKey))
            return;

        metadata[normalizedKey] = normalizedValue;
    }

    private static bool IsScopeMetadataKey(string key) =>
        string.Equals(key, "scope_id", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, WorkflowRunCommandMetadataKeys.ScopeId, StringComparison.OrdinalIgnoreCase);

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
            _ => WorkflowChatInputPartKind.Unspecified,
        };

        return kind != WorkflowChatInputPartKind.Unspecified;
    }
}
