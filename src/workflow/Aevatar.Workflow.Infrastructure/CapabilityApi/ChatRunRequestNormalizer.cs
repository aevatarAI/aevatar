using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using WorkflowProtocol = Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public readonly record struct ChatRunRequestNormalizationResult(
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
    private const string LegacyConnectorHttpAuthorizationBlockedKey = "connector.http.authorization";

    // Refactor (iter112/cluster-3): Old pattern: Host adapters populated Application mirror fields beside typed source. New principle: Host keeps wire aliases but normalizes them once into typed WorkflowChatSource.
    // Refactor (iter15/cluster-029):
    //   Old pattern: normalized context carried metadata-derived scope conflict state.
    //   New principle: scope is owned by the typed field; metadata only carries open extension entries.
    private readonly record struct NormalizedChatContext(
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyDictionary<string, string>? Headers,
        string? ScopeId);

    public static ChatRunRequestNormalizationResult Normalize(
        ChatInput input,
        IReadOnlyDictionary<string, string>? defaultMetadata = null,
        WorkflowCallerCredential? trustedCallerCredential = null,
        string? trustedScopeId = null,
        bool allowEmptyInputForResolvedMemberWorkflow = false)
    {
        // Refactor (iter112/cluster-3): Old pattern: host passed normalized legacy mirror fields into Application commands. New principle: host normalizes wire aliases once into typed WorkflowChatSource.
        // Refactor (iter349/cluster-349):
        //   Old pattern: WorkflowAuthoringSkillPromptAugmentor rewrote chat prompt via hidden workflow.authoring.enabled metadata + AEVATAR_WORKFLOW_AUTHORING_AUTO_INJECT env
        //   New principle: chat sources execute unchanged; hidden prompt mutation removed; explicit authoring surface (if needed) deferred to later-slice design
        ArgumentNullException.ThrowIfNull(input);

        return NormalizeWithInputParts(
            input,
            NormalizeInputParts(input.InputParts),
            defaultMetadata,
            trustedCallerCredential,
            trustedScopeId,
            allowEmptyInputForResolvedMemberWorkflow,
            chatConversation: null);
    }

    public static ChatRunRequestNormalizationResult Normalize(
        HttpChatInput input,
        IReadOnlyDictionary<string, string>? defaultMetadata = null,
        WorkflowCallerCredential? trustedCallerCredential = null,
        string? trustedScopeId = null,
        bool allowEmptyInputForResolvedMemberWorkflow = false)
    {
        ArgumentNullException.ThrowIfNull(input);

        var conversationResult = NormalizeConversation(input.Conversation);
        if (conversationResult.Error != WorkflowChatRunStartError.None)
            return ChatRunRequestNormalizationResult.Failed(conversationResult.Error);

        var chatInput = ToChatInput(input);
        return WithHttpCommandId(
            NormalizeWithInputParts(
                chatInput,
                NormalizeInputParts(input.InputParts),
                defaultMetadata,
                trustedCallerCredential,
                trustedScopeId,
                allowEmptyInputForResolvedMemberWorkflow,
                conversationResult.Conversation),
            input.CommandId);
    }

    private readonly record struct CallerCredentialNormalizationResult(
        WorkflowCallerCredential? Credential,
        WorkflowChatRunStartError Error);

    private static CallerCredentialNormalizationResult NormalizeCallerCredential(WorkflowCallerCredential? source)
    {
        if (source == null)
            return new CallerCredentialNormalizationResult(null, WorkflowChatRunStartError.None);

        var parsed = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(source.BearerToken);
        var sourceReadable = WorkflowProtocol.WorkflowCallerCredentialTokens.ParseOptional(
            source.SourceReadableUserBearerToken);
        if (WorkflowProtocol.WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                source.BearerToken,
                source.Kind,
                source.SourceReadableUserBearerToken))
            return new CallerCredentialNormalizationResult(null, WorkflowChatRunStartError.InvalidCallerCredential);

        var authority = NormalizeCallerNyxIdAuthority(source.NyxIdAuthority);
        if (parsed.IsMissing && authority == null)
            return new CallerCredentialNormalizationResult(null, WorkflowChatRunStartError.None);

        return new CallerCredentialNormalizationResult(
            new WorkflowCallerCredential(
                parsed.IsMissing ? null : parsed.NormalizedBearerToken,
                authority,
                source.Kind,
                sourceReadable.NormalizedBearerToken),
            WorkflowChatRunStartError.None);
    }

    private static WorkflowCallerNyxIdAuthority? NormalizeCallerNyxIdAuthority(
        WorkflowCallerNyxIdAuthority? authority)
    {
        if (authority == null)
            return null;

        var platform = NormalizeOptional(authority.Platform);
        var externalUserId = NormalizeOptional(authority.ExternalUserId);
        var scope = NormalizeOptional(authority.Scope);
        if (platform == null || externalUserId == null || scope == null)
            return null;

        return new WorkflowCallerNyxIdAuthority(
            platform,
            NormalizeOptional(authority.Tenant) ?? string.Empty,
            externalUserId,
            scope,
            NormalizeOptional(authority.BindingId));
    }

    private static WorkflowLlmControl? NormalizeLlmControl(ChatLlmControlInput? source)
    {
        if (source == null)
            return null;

        return new WorkflowLlmControl(
            ModelOverride: NormalizeOptional(source.ModelOverride),
            MaxToolRoundsOverride: source.MaxToolRoundsOverride is > 0 ? source.MaxToolRoundsOverride : null,
            UserMemoryPrompt: NormalizeOptional(source.UserMemoryPrompt),
            RoutePreference: NormalizeOptional(source.NyxIdRoutePreference));
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

    public static async ValueTask<ChatRunRequestNormalizationResult> NormalizeAsync(
        ChatInput input,
        IFileArtifactIngressPort? fileIngressPort,
        IReadOnlyDictionary<string, string>? defaultMetadata = null,
        WorkflowCallerCredential? trustedCallerCredential = null,
        CancellationToken cancellationToken = default,
        string? trustedScopeId = null,
        bool allowEmptyInputForResolvedMemberWorkflow = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalizedInputParts = fileIngressPort == null
            ? NormalizeInputParts(input.InputParts)
            : await NormalizeInputPartsAsync(input.InputParts, fileIngressPort, cancellationToken);
        return NormalizeWithInputParts(
            input,
            normalizedInputParts,
            defaultMetadata,
            trustedCallerCredential,
            trustedScopeId,
            allowEmptyInputForResolvedMemberWorkflow,
            chatConversation: null);
    }

    public static async ValueTask<ChatRunRequestNormalizationResult> NormalizeAsync(
        HttpChatInput input,
        IFileArtifactIngressPort? fileIngressPort,
        IReadOnlyDictionary<string, string>? defaultMetadata = null,
        WorkflowCallerCredential? trustedCallerCredential = null,
        CancellationToken cancellationToken = default,
        string? trustedScopeId = null,
        bool allowEmptyInputForResolvedMemberWorkflow = false)
    {
        ArgumentNullException.ThrowIfNull(input);

        var conversationResult = NormalizeConversation(input.Conversation);
        if (conversationResult.Error != WorkflowChatRunStartError.None)
            return ChatRunRequestNormalizationResult.Failed(conversationResult.Error);

        var normalizedInputParts = fileIngressPort == null
            ? NormalizeInputParts(input.InputParts)
            : await NormalizeInputPartsAsync(input.InputParts, fileIngressPort, cancellationToken);
        return WithHttpCommandId(
            NormalizeWithInputParts(
                ToChatInput(input),
                normalizedInputParts,
                defaultMetadata,
                trustedCallerCredential,
                trustedScopeId,
                allowEmptyInputForResolvedMemberWorkflow,
                conversationResult.Conversation),
            input.CommandId);
    }

    private static ChatRunRequestNormalizationResult NormalizeWithInputParts(
        ChatInput input,
        InputPartsNormalizationResult normalizedInputPartsResult,
        IReadOnlyDictionary<string, string>? defaultMetadata,
        WorkflowCallerCredential? trustedCallerCredential,
        string? trustedScopeId,
        bool allowEmptyInputForResolvedMemberWorkflow,
        WorkflowChatConversationIntent? chatConversation)
    {
        if (normalizedInputPartsResult.Error != WorkflowChatRunStartError.None)
            return ChatRunRequestNormalizationResult.Failed(normalizedInputPartsResult.Error);

        var normalizedInputParts = normalizedInputPartsResult.InputParts;
        if (HasOnlyUnsupportedInputParts(input, normalizedInputParts))
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.PromptRequired);

        // 06-20-observatory-run-state-feed (R2d): when the ingress resolved a scope from the authenticated
        // caller claim, that claim is authoritative for the run's scope_id. Without a trusted caller scope,
        // preserve the explicit ChatInput scope for non-HTTP command/bridge paths.
        var effectiveScopeId = string.IsNullOrWhiteSpace(trustedScopeId) ? input.ScopeId : trustedScopeId;
        var normalizedContext = NormalizeContext(effectiveScopeId, input.Metadata, input.Headers, defaultMetadata);
        var normalizedMetadata = normalizedContext.Metadata;
        var sourceResult = NormalizeSource(input);
        if (!sourceResult.Succeeded)
            return ChatRunRequestNormalizationResult.Failed(sourceResult.Error);

        var rawPrompt = ResolvePrompt(input.Prompt, normalizedInputParts);
        if (rawPrompt.Length == 0 &&
            !CanStartWithoutInput(sourceResult.Source!, allowEmptyInputForResolvedMemberWorkflow))
            return ChatRunRequestNormalizationResult.Failed(WorkflowChatRunStartError.PromptRequired);

        var callerCredentialResult = NormalizeCallerCredential(trustedCallerCredential);
        if (callerCredentialResult.Error != WorkflowChatRunStartError.None)
            return ChatRunRequestNormalizationResult.Failed(callerCredentialResult.Error);

        return ChatRunRequestNormalizationResult.Success(
            new WorkflowChatRunRequest(
                Prompt: rawPrompt,
                Source: sourceResult.Source!,
                ExpectedExecutionMode: WorkflowProtocol.ExternalCapabilityExecutionMode.Interactive,
                SessionId: NormalizeSessionId(input.SessionId),
                InputParts: normalizedInputParts,
                Metadata: normalizedMetadata,
                ScopeId: normalizedContext.ScopeId,
                LlmControl: NormalizeLlmControl(input.LlmControl),
                CallerCredential: callerCredentialResult.Credential,
                Headers: normalizedContext.Headers,
                ChatConversation: chatConversation));
    }

    private readonly record struct ConversationNormalizationResult(
        WorkflowChatConversationIntent? Conversation,
        WorkflowChatRunStartError Error);

    private static ConversationNormalizationResult NormalizeConversation(ChatConversationInput? source)
    {
        if (source == null)
            return new ConversationNormalizationResult(null, WorkflowChatRunStartError.None);

        if (source.ConversationId == null)
            return new ConversationNormalizationResult(
                WorkflowChatConversationIntent.Create(),
                WorkflowChatRunStartError.None);

        var conversationId = NormalizeOptional(source.ConversationId);
        if (conversationId == null)
            return new ConversationNormalizationResult(null, WorkflowChatRunStartError.InvalidConversationId);
        if (source.MinimumStateVersion is not > 0)
            return new ConversationNormalizationResult(null, WorkflowChatRunStartError.ChatHistoryReservationUnavailable);

        return new ConversationNormalizationResult(
            WorkflowChatConversationIntent.Continue(
                conversationId,
                source.MinimumStateVersion),
            WorkflowChatRunStartError.None);
    }

    private static ChatInput ToChatInput(HttpChatInput input) =>
        new()
        {
            Prompt = input.Prompt,
            InputParts = input.InputParts,
            Source = input.Source,
            Workflow = input.Workflow,
            SessionId = input.SessionId,
            WorkflowYaml = input.WorkflowYaml,
            WorkflowYamls = input.WorkflowYamls,
            Metadata = input.Metadata,
            Headers = input.Headers,
            LlmControl = input.LlmControl,
            ToolContext = input.ToolContext,
        };

    private static ChatRunRequestNormalizationResult WithHttpCommandId(
        ChatRunRequestNormalizationResult result,
        string? commandId)
    {
        if (!result.Succeeded || result.Request == null)
            return result;

        var normalizedCommandId = NormalizeOptional(commandId);
        return normalizedCommandId == null
            ? result
            : ChatRunRequestNormalizationResult.Success(result.Request with
            {
                CommandIdSeed = normalizedCommandId,
            });
    }

    private static SourceNormalizationResult NormalizeSource(ChatInput input)
    {
        // Refactor (phase9/cluster-349):
        //   Old pattern: legacy workflow inputs could smuggle actor authority through top-level agentId.
        //   New principle: legacy name/YAML aliases resolve only workflow content; actor authority must be in typed source variants.
        if (input.Source != null)
            return NormalizeTypedSource(input.Source);

        var requestedWorkflowName = NormalizeWorkflowName(input.Workflow);
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
            return SourceNormalizationResult.Success(ToInlineYamlBundleSource(
                string.IsNullOrWhiteSpace(requestedWorkflowName) ? null : requestedWorkflowName,
                inlineWorkflowYamls,
                actorId: null));

        if (!string.IsNullOrWhiteSpace(requestedWorkflowName))
            return SourceNormalizationResult.Success(WorkflowChatSource.CatalogWorkflow(requestedWorkflowName));

        return SourceNormalizationResult.Success(WorkflowChatSource.Direct());
    }

    private static SourceNormalizationResult NormalizeTypedSource(WorkflowChatSourceInput source)
    {
        // Refactor (phase9/cluster-349):
        //   Old pattern: typed direct source accepted actor id aliases and built Direct(actorId).
        //   New principle: direct source is address-free; actor ids belong to DefinitionActor.
        var kind = NormalizeSourceKind(source.Kind);
        var workflowName = ResolveTypedWorkflowName(source, kind);
        var actorId = ResolveTypedActorId(source, kind);
        var inlineYamlDocuments = ResolveTypedInlineYamlDocuments(source, kind);

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
            WorkflowChatSourceKind.InlineYamlBundle when inlineYamlDocuments.Count == 0 ||
                                                          inlineYamlDocuments.Any(static document => string.IsNullOrWhiteSpace(document.Yaml)) =>
                SourceNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml),
            WorkflowChatSourceKind.InlineYamlBundle =>
                SourceNormalizationResult.Success(WorkflowChatSource.InlineYamlBundle(
                    string.IsNullOrWhiteSpace(workflowName) ? null : workflowName,
                    inlineYamlDocuments,
                    string.IsNullOrWhiteSpace(actorId) ? null : actorId)),
            WorkflowChatSourceKind.Direct when !string.IsNullOrWhiteSpace(actorId) =>
                SourceNormalizationResult.Failed(WorkflowChatRunStartError.InvalidWorkflowYaml),
            WorkflowChatSourceKind.Direct =>
                SourceNormalizationResult.Success(WorkflowChatSource.Direct()),
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

    private static string ResolveTypedWorkflowName(WorkflowChatSourceInput source, WorkflowChatSourceKind kind)
    {
        var value = kind switch
        {
            WorkflowChatSourceKind.CatalogWorkflow => source.CatalogName?.WorkflowName ?? source.WorkflowName,
            WorkflowChatSourceKind.DefinitionActor => source.DefinitionActor?.WorkflowName ?? source.WorkflowName,
            WorkflowChatSourceKind.InlineYamlBundle => source.InlineBundle?.EntryName ?? source.WorkflowName,
            _ => source.WorkflowName,
        };
        return NormalizeWorkflowName(value);
    }

    private static string ResolveTypedActorId(WorkflowChatSourceInput source, WorkflowChatSourceKind kind)
    {
        // Refactor (phase9/cluster-349):
        //   Old pattern: source.actorId was a catch-all fallback for definition actor, inline bundle, and direct source.
        //   New principle: each source kind reads only its typed actor-id slot; direct/catalog sources stay address-free.
        var value = kind switch
        {
            WorkflowChatSourceKind.DefinitionActor => source.DefinitionActor?.ActorId,
            WorkflowChatSourceKind.InlineYamlBundle => source.InlineBundle?.ActorId,
            _ => null,
        };
        return NormalizeActorId(value);
    }

    private static IReadOnlyList<WorkflowChatInlineYamlDocument> ResolveTypedInlineYamlDocuments(
        WorkflowChatSourceInput source,
        WorkflowChatSourceKind kind)
    {
        if (kind != WorkflowChatSourceKind.InlineYamlBundle)
            return ToUnnamedInlineYamlDocuments(NormalizeInlineWorkflowYamls(source.WorkflowYamls));

        if (source.InlineBundle?.YamlDocuments is not { Count: > 0 } documents)
            return ToUnnamedInlineYamlDocuments(NormalizeInlineWorkflowYamls(source.WorkflowYamls));

        return documents.Select(static document => new WorkflowChatInlineYamlDocument(
            string.IsNullOrWhiteSpace(document?.Name) ? string.Empty : document.Name.Trim(),
            document?.Yaml ?? string.Empty)).ToArray();
    }

    private static WorkflowChatSource ToInlineYamlBundleSource(
        string? entryName,
        IReadOnlyList<string> workflowYamls,
        string? actorId)
    {
        var documents = ToUnnamedInlineYamlDocuments(workflowYamls);
        return WorkflowChatSource.InlineYamlBundle(entryName, documents, actorId);
    }

    private static IReadOnlyList<WorkflowChatInlineYamlDocument> ToUnnamedInlineYamlDocuments(
        IReadOnlyList<string> workflowYamls) =>
        workflowYamls.Select(static yaml => new WorkflowChatInlineYamlDocument(string.Empty, yaml)).ToArray();

    private static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName) ? string.Empty : workflowName.Trim();

    private readonly record struct InputPartsNormalizationResult(
        IReadOnlyList<WorkflowChatInputPart>? InputParts,
        WorkflowChatRunStartError Error);

    private static async ValueTask<InputPartsNormalizationResult> NormalizeInputPartsAsync(
        IReadOnlyList<ChatInputContentPart>? inputParts,
        IFileArtifactIngressPort fileIngressPort,
        CancellationToken cancellationToken)
    {
        if (inputParts == null || inputParts.Count == 0)
            return new InputPartsNormalizationResult(null, WorkflowChatRunStartError.None);

        var ingested = new List<WorkflowChatInputPart>(inputParts.Count);
        foreach (var part in inputParts)
        {
            if (part == null)
                continue;

            var fileInputResult = await NormalizeFileInputAsync(part, fileIngressPort, cancellationToken);
            if (fileInputResult.Error != WorkflowChatRunStartError.None)
                return new InputPartsNormalizationResult(null, fileInputResult.Error);

            if (string.IsNullOrWhiteSpace(part.Type))
                continue;

            if (!TryParseContentPartKind(part.Type, out var kind))
                continue;

            ingested.Add(BuildWorkflowInputPart(part, kind, fileInputResult));
        }

        return new InputPartsNormalizationResult(
            ingested.Count == 0 ? null : ingested,
            WorkflowChatRunStartError.None);
    }

    private static InputPartsNormalizationResult NormalizeInputParts(IReadOnlyList<ChatInputContentPart>? inputParts)
    {
        if (inputParts == null || inputParts.Count == 0)
            return new InputPartsNormalizationResult(null, WorkflowChatRunStartError.None);

        var normalized = new List<WorkflowChatInputPart>(inputParts.Count);
        foreach (var part in inputParts)
        {
            if (part == null)
                continue;

            var fileInputResult = NormalizeFileInput(part);
            if (fileInputResult.Error != WorkflowChatRunStartError.None)
                return new InputPartsNormalizationResult(null, fileInputResult.Error);

            if (string.IsNullOrWhiteSpace(part.Type))
                continue;

            if (!TryParseContentPartKind(part.Type, out var kind))
                continue;

            normalized.Add(BuildWorkflowInputPart(part, kind, fileInputResult));
        }

        return new InputPartsNormalizationResult(
            normalized.Count == 0 ? null : normalized,
            WorkflowChatRunStartError.None);
    }

    private readonly record struct FileInputNormalizationResult(
        string? DataBase64,
        string? MediaType,
        string? Uri,
        string? Name,
        FileArtifactRef? FileRef,
        WorkflowChatRunStartError Error);

    private static WorkflowChatInputPart BuildWorkflowInputPart(
        ChatInputContentPart part,
        WorkflowChatInputPartKind kind,
        FileInputNormalizationResult fileInputResult)
    {
        if (fileInputResult.FileRef is not null)
        {
            var filePart = WorkflowChatInputParts.FromFileRef(fileInputResult.FileRef, kind);
            return filePart with
            {
                Text = string.IsNullOrWhiteSpace(part.Text) ? null : part.Text,
                MediaType = filePart.MediaType ?? NormalizeContentPartValue(part.MediaType),
                Uri = filePart.Uri ?? NormalizeContentPartValue(part.Uri),
                Name = filePart.Name ?? NormalizeContentPartValue(part.Name),
            };
        }

        return new WorkflowChatInputPart
        {
            Kind = kind,
            Text = string.IsNullOrWhiteSpace(part.Text) ? null : part.Text,
            DataBase64 = fileInputResult.DataBase64 ?? NormalizeContentPartValue(part.DataBase64),
            MediaType = fileInputResult.MediaType ?? NormalizeContentPartValue(part.MediaType),
            Uri = fileInputResult.Uri ?? NormalizeContentPartValue(part.Uri),
            Name = fileInputResult.Name ?? NormalizeContentPartValue(part.Name),
        };
    }

    private static FileInputNormalizationResult NormalizeFileInput(ChatInputContentPart part)
    {
        if (part.InlineFile != null && part.FileRef != null)
            return InvalidFileInput();

        if (part.InlineFile != null)
            return NormalizeInlineFile(part.InlineFile);

        if (part.FileRef != null)
            return NormalizeFileRef(part.FileRef);

        return new FileInputNormalizationResult(null, null, null, null, null, WorkflowChatRunStartError.None);
    }

    private static async ValueTask<FileInputNormalizationResult> NormalizeFileInputAsync(
        ChatInputContentPart part,
        IFileArtifactIngressPort fileIngressPort,
        CancellationToken cancellationToken)
    {
        if (part.InlineFile != null && part.FileRef != null)
            return InvalidFileInput();

        if (part.InlineFile != null)
            return await NormalizeInlineFileAsync(part.InlineFile, fileIngressPort, cancellationToken);

        if (part.FileRef != null)
            return NormalizeFileRef(part.FileRef);

        return new FileInputNormalizationResult(null, null, null, null, null, WorkflowChatRunStartError.None);
    }

    private static FileInputNormalizationResult NormalizeInlineFile(ChatInputInlineFile inlineFile)
    {
        var dataBase64 = NormalizeOptional(inlineFile.DataBase64);
        if (dataBase64 == null)
            return InvalidFileInput();

        if (!TryGetDecodedByteLength(dataBase64, out var decodedByteLength))
            return InvalidFileInput();

        if (inlineFile.SizeBytes.HasValue)
        {
            if (inlineFile.SizeBytes.Value < 0)
                return InvalidFileInput();

            if (inlineFile.SizeBytes.Value != decodedByteLength)
                return InvalidFileInput();
        }

        return new FileInputNormalizationResult(
            dataBase64,
            NormalizeContentPartValue(inlineFile.MediaType),
            null,
            NormalizeContentPartValue(inlineFile.Name),
            null,
            WorkflowChatRunStartError.None);
    }

    private static async ValueTask<FileInputNormalizationResult> NormalizeInlineFileAsync(
        ChatInputInlineFile inlineFile,
        IFileArtifactIngressPort fileIngressPort,
        CancellationToken cancellationToken)
    {
        var dataBase64 = NormalizeOptional(inlineFile.DataBase64);
        if (dataBase64 == null)
            return InvalidFileInput();

        if (!TryDecodeBase64(dataBase64, out var content))
            return InvalidFileInput();

        if (inlineFile.SizeBytes.HasValue)
        {
            if (inlineFile.SizeBytes.Value < 0)
                return InvalidFileInput();

            if (inlineFile.SizeBytes.Value != content.LongLength)
                return InvalidFileInput();
        }

        var result = await fileIngressPort.IngestAsync(
            new FileArtifactIngressRequest(
                content,
                FileArtifactSourceKind.ChatInput,
                FileName: NormalizeContentPartValue(inlineFile.Name),
                MediaType: NormalizeContentPartValue(inlineFile.MediaType),
                OwnerScopeId: NormalizeContentPartValue(inlineFile.OwnerScopeId)),
            cancellationToken);

        return new FileInputNormalizationResult(
            null,
            result.FileRef.MediaType,
            result.FileRef.ArtifactId,
            result.FileRef.FileName,
            result.FileRef,
            WorkflowChatRunStartError.None);
    }

    private static FileInputNormalizationResult NormalizeFileRef(ChatInputFileRef fileRef)
    {
        var artifactId = NormalizeContentPartValue(fileRef.ArtifactId) ??
                         NormalizeContentPartValue(fileRef.Uri);
        var fileId = NormalizeContentPartValue(fileRef.FileId);
        if (fileId == null && artifactId == null)
            return InvalidFileInput();

        if (!TryNormalizeFileSourceKind(fileRef.SourceKind, out var sourceKind))
            return InvalidFileInput();

        if (!TryNormalizeUnixMs(fileRef.CreatedAtUnixMs, out var createdAtUnixMs) ||
            !TryNormalizeUnixMs(fileRef.ExpiresAtUnixMs, out var expiresAtUnixMs))
            return InvalidFileInput();

        if (createdAtUnixMs > 0 && expiresAtUnixMs > 0 && expiresAtUnixMs < createdAtUnixMs)
            return InvalidFileInput();

        var mediaType = NormalizeContentPartValue(fileRef.MediaType);
        var fileName = NormalizeContentPartValue(fileRef.FileName) ??
                       NormalizeContentPartValue(fileRef.Name);

        var normalized = new FileArtifactRef
        {
            FileId = fileId,
            ArtifactId = artifactId,
            SourceKind = sourceKind,
            SourceMessageId = NormalizeContentPartValue(fileRef.SourceMessageId),
            SourceResourceKey = NormalizeContentPartValue(fileRef.SourceResourceKey),
            FileName = fileName,
            MediaType = mediaType,
            Sha256 = NormalizeContentPartValue(fileRef.Sha256),
            CreatedAtUnixMs = createdAtUnixMs,
            ExpiresAtUnixMs = expiresAtUnixMs,
            OwnerRunId = NormalizeContentPartValue(fileRef.OwnerRunId),
            OwnerScopeId = NormalizeContentPartValue(fileRef.OwnerScopeId),
        };

        return new FileInputNormalizationResult(
            null,
            mediaType,
            artifactId,
            fileName,
            normalized,
            WorkflowChatRunStartError.None);
    }

    private static bool TryNormalizeFileSourceKind(string? sourceKind, out FileArtifactSourceKind normalized)
    {
        normalized = FileArtifactSourceKind.Unspecified;

        var value = NormalizeContentPartValue(sourceKind);
        if (value == null)
            return true;

        var sourceKindKey = value.ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
        normalized = sourceKindKey switch
        {
            "unspecified" =>
                FileArtifactSourceKind.Unspecified,
            "chatinput" or "chat" =>
                FileArtifactSourceKind.ChatInput,
            "formupload" or "form" =>
                FileArtifactSourceKind.FormUpload,
            "connectedserviceresource" or "connectedservice" =>
                FileArtifactSourceKind.ConnectedServiceResource,
            "externalresource" or "external" =>
                FileArtifactSourceKind.ExternalResource,
            "generated" =>
                FileArtifactSourceKind.Generated,
            _ => FileArtifactSourceKind.Unspecified,
        };

        return normalized != FileArtifactSourceKind.Unspecified || sourceKindKey == "unspecified";
    }

    private static bool TryNormalizeUnixMs(long? value, out long normalized)
    {
        normalized = value ?? 0;
        return normalized >= 0;
    }

    private static FileInputNormalizationResult InvalidFileInput() =>
        new(null, null, null, null, null, WorkflowChatRunStartError.InvalidFileInput);

    private static bool TryGetDecodedByteLength(string dataBase64, out long decodedByteLength)
    {
        long base64Length = 0;
        var padding = 0;
        var hasPadding = false;

        foreach (var ch in dataBase64)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            if (ch == '=')
            {
                hasPadding = true;
                padding++;
                base64Length++;
                if (padding > 2)
                {
                    decodedByteLength = 0;
                    return false;
                }

                continue;
            }

            if (hasPadding || !IsBase64Character(ch))
            {
                decodedByteLength = 0;
                return false;
            }

            base64Length++;
        }

        if (base64Length == 0 || base64Length % 4 != 0)
        {
            decodedByteLength = 0;
            return false;
        }

        decodedByteLength = (base64Length / 4 * 3) - padding;
        return true;
    }

    private static bool IsBase64Character(char ch) =>
        ch is >= 'A' and <= 'Z' ||
        ch is >= 'a' and <= 'z' ||
        ch is >= '0' and <= '9' ||
        ch is '+' or '/';

    private static bool TryDecodeBase64(string dataBase64, out byte[] content)
    {
        content = [];
        if (string.IsNullOrWhiteSpace(dataBase64))
            return false;

        try
        {
            content = Convert.FromBase64String(dataBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        return content.Length > 0;
    }

    private static string? NormalizeContentPartValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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
        IDictionary<string, string>? headers,
        IReadOnlyDictionary<string, string>? defaultMetadata)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalizedHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
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

        if (headers is { Count: > 0 })
        {
            foreach (var (key, value) in headers)
                AddNormalizedMetadataEntry(normalizedHeaders, key, value);
        }

        return new NormalizedChatContext(
            normalized,
            normalizedHeaders.Count == 0 ? null : normalizedHeaders,
            normalizedScopeId);
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

        if (IsReservedMetadataKey(normalizedKey))
            return;

        metadata[normalizedKey] = normalizedValue;
    }

    private static bool IsReservedMetadataKey(string key) =>
        IsScopeMetadataKey(key) ||
        string.Equals(key, LegacyConnectorHttpAuthorizationBlockedKey, StringComparison.Ordinal);

    private static bool IsScopeMetadataKey(string key) =>
        string.Equals(key, "scope_id", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, WorkflowRunCommandMetadataKeys.ScopeId, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeScopeId(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId) ? null : scopeId.Trim();

    private static string NormalizeActorId(string? actorId) =>
        string.IsNullOrWhiteSpace(actorId) ? string.Empty : actorId.Trim();

    private static string? NormalizeSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();

    private static bool CanStartWithoutInput(
        WorkflowChatSource source,
        bool allowEmptyInputForResolvedMemberWorkflow) =>
        allowEmptyInputForResolvedMemberWorkflow &&
        source.Kind == WorkflowChatSourceKind.DefinitionActor &&
        !string.IsNullOrWhiteSpace(source.DefinitionActorSource?.ActorId);

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
                WorkflowChatInputPartKind.File => "[file]",
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
            "file" => WorkflowChatInputPartKind.File,
            _ => WorkflowChatInputPartKind.Unspecified,
        };

        return kind != WorkflowChatInputPartKind.Unspecified;
    }
}
