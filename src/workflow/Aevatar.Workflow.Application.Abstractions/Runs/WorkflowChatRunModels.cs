using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions;
using System.Text.Json.Serialization;

namespace Aevatar.Workflow.Application.Abstractions.Runs;

public enum WorkflowChatInputPartKind
{
    Unspecified = 0,
    Text = 1,
    Image = 2,
    Audio = 3,
    Video = 4,
    File = 5,
}

public enum FileArtifactSourceKind
{
    Unspecified = 0,
    ChatInput = 1,
    FormUpload = 2,
    ConnectedServiceResource = 3,
    ExternalResource = 4,
    Generated = 5,
}

public sealed record FileArtifactRef
{
    public string? FileId { get; init; }
    public string? ArtifactId { get; init; }
    public FileArtifactSourceKind SourceKind { get; init; }
    public string? SourceMessageId { get; init; }
    public string? SourceResourceKey { get; init; }
    public string? FileName { get; init; }
    public string? MediaType { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public long CreatedAtUnixMs { get; init; }
    public long ExpiresAtUnixMs { get; init; }
    public string? OwnerRunId { get; init; }
    public string? OwnerScopeId { get; init; }
}

public sealed record WorkflowChatInputPart
{
    public required WorkflowChatInputPartKind Kind { get; init; }
    public string? Text { get; init; }
    public string? DataBase64 { get; init; }
    public string? MediaType { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public FileArtifactRef? FileRef { get; init; }
}

public sealed record WorkflowLlmControl(
    string? ModelOverride = null,
    int? MaxToolRoundsOverride = null,
    string? UserMemoryPrompt = null,
    string? RoutePreference = null,
    string? SenderNyxIdAccessToken = null);

public sealed record WorkflowCallerNyxIdAuthority(
    string Platform,
    string Tenant,
    string ExternalUserId,
    string Scope,
    string? BindingId = null);

public sealed record WorkflowCallerCredential(
    string? BearerToken = null,
    WorkflowCallerNyxIdAuthority? NyxIdAuthority = null,
    Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind Kind =
        Aevatar.Workflow.Abstractions.NyxIdCallerCredentialKind.Unspecified,
    string? SourceReadableUserBearerToken = null,
    // Ingress-only seal. The Run validates this against its actor-owned exact
    // definition and stores it outside the generic caller credential state.
    Aevatar.Workflow.Abstractions.WorkflowUnattendedEffectAuthorization?
        UnattendedEffectAuthorization = null,
    [property: JsonIgnore] DurableCallerCredentialRef? DurableCallerCredential = null)
{
    public override string ToString() =>
        $"{nameof(WorkflowCallerCredential)} {{ BearerToken = [REDACTED], SourceReadableUserBearerToken = [REDACTED], NyxIdAuthorityPresent = {NyxIdAuthority is not null}, UnattendedEffectAuthorizationPresent = {UnattendedEffectAuthorization is not null}, DurableCallerCredentialPresent = {DurableCallerCredential is not null} }}";
}

public sealed record WorkflowExternalIngressContext(
    string RouteKey,
    string SourceId,
    string DeliveryId,
    long ReceivedAtUnixMs,
    string? ContentType = null,
    string? PayloadFingerprint = null,
    string? AuthScheme = null,
    string? PrincipalSubject = null);

public enum WorkflowConversationExecutionRole
{
    Unspecified = 0,
    User = 1,
    Assistant = 2,
    Tool = 3,
}

public sealed record WorkflowConversationExecutionMessage(
    int Sequence,
    string TurnId,
    WorkflowConversationExecutionRole Role,
    string Content);

public sealed record WorkflowConversationExecutionContext(
    string ScopeId,
    string ConversationId,
    long StateVersion,
    IReadOnlyList<WorkflowConversationExecutionMessage> Messages,
    bool Truncated,
    int MaxMessageCount,
    string CurrentTurnId = "");

public enum WorkflowChatConversationIntentKind
{
    None = 0,
    Create = 1,
    Continue = 2,
}

public sealed record WorkflowChatConversationIntent(
    WorkflowChatConversationIntentKind Intent,
    string? ConversationId = null,
    long? MinimumStateVersion = null)
{
    public static WorkflowChatConversationIntent None() =>
        new(WorkflowChatConversationIntentKind.None);

    public static WorkflowChatConversationIntent Create() =>
        new(WorkflowChatConversationIntentKind.Create);

    public static WorkflowChatConversationIntent Continue(
        string conversationId,
        long? minimumStateVersion = null) =>
        new(
            WorkflowChatConversationIntentKind.Continue,
            conversationId,
            minimumStateVersion is > 0 ? minimumStateVersion : null);
}

public sealed record WorkflowCompletionNotificationTarget(
    string ActorId,
    string DeliveryId,
    long ExpiresAtUnixMs);

public sealed record WorkflowChatRunForkSeed(
    string SourceRunId,
    string StartAtStepId,
    IReadOnlyDictionary<string, string> Variables,
    int Attempt = 0,
    WorkflowStepIdempotencyView? StartStepIdempotency = null,
    string OriginalRunId = "",
    WorkflowNormalizedExecutionSeed? NormalizedValues = null,
    IReadOnlyDictionary<string, string>? VariableOverrides = null);

public enum WorkflowChatSourceKind
{
    Unspecified = 0,
    CatalogWorkflow = 1,
    DefinitionActor = 2,
    InlineYamlBundle = 3,
    Direct = 4,
}

public sealed record WorkflowChatCatalogNameSource(string WorkflowName);

public sealed record WorkflowChatDefinitionActorSource(string ActorId, string? WorkflowName = null);

public sealed record WorkflowChatInlineYamlDocument(string Name, string Yaml);

public sealed record WorkflowChatInlineYamlBundleSource(
    string? EntryName,
    IReadOnlyList<WorkflowChatInlineYamlDocument> YamlDocuments,
    string? ActorId = null);

// Refactor (iter165/cluster-007):
//   Old pattern: InlineYamlBundle reused WorkflowName, ActorId, and WorkflowYamls on the parent source, so one field meant lookup identity or inline content depending on Kind.
//   New principle: each source variant owns a single-purpose typed submessage; legacy parent properties are read-only migration views.
public sealed record WorkflowChatSource
{
    public WorkflowChatSource(
        WorkflowChatSourceKind kind,
        WorkflowChatCatalogNameSource? catalogName = null,
        WorkflowChatDefinitionActorSource? definitionActorSource = null,
        WorkflowChatInlineYamlBundleSource? inlineBundle = null)
    {
        Kind = kind;
        CatalogName = catalogName;
        DefinitionActorSource = definitionActorSource;
        InlineBundle = inlineBundle;
    }

    public WorkflowChatSourceKind Kind { get; init; }
    public WorkflowChatCatalogNameSource? CatalogName { get; init; }
    public WorkflowChatDefinitionActorSource? DefinitionActorSource { get; init; }
    public WorkflowChatInlineYamlBundleSource? InlineBundle { get; init; }

    public string? WorkflowName => Kind switch
    {
        WorkflowChatSourceKind.CatalogWorkflow => CatalogName?.WorkflowName,
        WorkflowChatSourceKind.DefinitionActor => DefinitionActorSource?.WorkflowName,
        WorkflowChatSourceKind.InlineYamlBundle => InlineBundle?.EntryName,
        _ => null,
    };

    public string? ActorId => Kind switch
    {
        WorkflowChatSourceKind.DefinitionActor => DefinitionActorSource?.ActorId,
        WorkflowChatSourceKind.InlineYamlBundle => InlineBundle?.ActorId,
        _ => null,
    };

    public IReadOnlyList<string>? WorkflowYamls =>
        InlineBundle?.YamlDocuments.Select(static document => document.Yaml).ToArray();

    public static WorkflowChatSource CatalogWorkflow(string workflowName) =>
        new(
            WorkflowChatSourceKind.CatalogWorkflow,
            catalogName: new WorkflowChatCatalogNameSource(workflowName));

    public static WorkflowChatSource DefinitionActor(string actorId, string? workflowName = null) =>
        new(
            WorkflowChatSourceKind.DefinitionActor,
            definitionActorSource: new WorkflowChatDefinitionActorSource(actorId, workflowName));

    public static WorkflowChatSource InlineYamlBundle(
        string? entryName,
        IReadOnlyList<WorkflowChatInlineYamlDocument> yamlDocuments,
        string? actorId = null) =>
        new(
            WorkflowChatSourceKind.InlineYamlBundle,
            inlineBundle: new WorkflowChatInlineYamlBundleSource(entryName, yamlDocuments, actorId));

    public static WorkflowChatSource InlineYamlBundle(IReadOnlyList<string> workflowYamls, string? workflowName = null, string? actorId = null) =>
        InlineYamlBundle(
            workflowName,
            workflowYamls.Select(static (yaml, index) => new WorkflowChatInlineYamlDocument(
                string.Empty,
                yaml)).ToArray(),
            actorId);

    // Refactor (phase9/cluster-349):
    //   Old pattern: Direct reused DefinitionActorSource to smuggle an actor address.
    //   New principle: Direct is address-free; actor-targeted execution uses DefinitionActor.
    public static WorkflowChatSource Direct() =>
        new(WorkflowChatSourceKind.Direct);
}

// Refactor (iter112/cluster-3): Old pattern: application commands carried typed source plus legacy mirror fields. New principle: Application owns one typed WorkflowChatSource representation.
public sealed record WorkflowChatRunRequest(
    string Prompt,
    WorkflowChatSource Source,
    ExternalCapabilityExecutionMode ExpectedExecutionMode,
    string? SessionId = null,
    IReadOnlyList<WorkflowChatInputPart>? InputParts = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    // Refactor (iter15/cluster-029):
    //   Old pattern: scope id / channel facts fell back to metadata bag string keys.
    //   New principle: stable business semantics use typed proto field; metadata bag only for genuine open extension.
    string? ScopeId = null,
    WorkflowLlmControl? LlmControl = null,
    WorkflowCallerCredential? CallerCredential = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? CommandIdSeed = null,
    string? CorrelationIdSeed = null,
    WorkflowChatRunForkSeed? ForkSeed = null,
    WorkflowExternalIngressContext? ExternalIngress = null,
    WorkflowChatConversationIntent? ChatConversation = null,
    WorkflowConversationExecutionContext? ConversationContext = null,
    [property: JsonIgnore] string? CurrentTurnId = null,
    [property: JsonIgnore] WorkflowRunTargetSeed? TargetSeed = null,
    [property: JsonIgnore] WorkflowCompletionNotificationTarget? CompletionNotificationTarget = null,
    [property: JsonIgnore] WorkflowDefinitionBinding? ResolvedDefinitionBinding = null,
    [property: JsonIgnore] NyxIdCallerCredentialSelection? CallerNyxIdCredentialSelection = null) : ICommandContextSeed
{
    string? ICommandContextSeed.CommandId => CommandIdSeed;

    string? ICommandContextSeed.CorrelationId => CorrelationIdSeed;

    IReadOnlyDictionary<string, string>? ICommandContextSeed.Headers => Headers;
}

public sealed record WorkflowRunTargetSeed(
    string ActorId,
    string WorkflowNameForRun,
    IReadOnlyList<string>? CreatedActorIds = null,
    WorkflowChatSource? Source = null);

public enum WorkflowChatRunStartError
{
    None = 0,
    AgentNotFound = 1,
    WorkflowNotFound = 2,
    AgentTypeNotSupported = 3,
    ProjectionDisabled = 4,
    WorkflowBindingMismatch = 5,
    AgentWorkflowNotConfigured = 6,
    InvalidWorkflowYaml = 7,
    WorkflowNameMismatch = 8,
    PromptRequired = 9,
    ProjectionUnavailable = 10,
    InvalidCallerCredential = 11,
    InvalidFileInput = 12,
    ExternalCapabilityNotReady = 13,
    InvalidConversationInput = 14,
    InvalidConversationId = 15,
    ConversationNotFound = 16,
    ChatHistoryReservationUnavailable = 17,
    IdempotencyConflict = 18,
}

public enum WorkflowProjectionCompletionStatus
{
    Completed = 0,
    TimedOut = 1,
    Failed = 2,
    Stopped = 3,
    NotFound = 4,
    Disabled = 5,
    Unknown = 99,
}

public sealed record WorkflowChatRunAcceptedReceipt(
    string ActorId,
    string WorkflowName,
    string CommandId,
    string CorrelationId);

public sealed record WorkflowChatContext(
    string ScopeId,
    string ConversationId,
    string TurnId,
    long StateVersion = 0);

public sealed record WorkflowChatInteractionAcceptedReceipt(
    WorkflowChatRunAcceptedReceipt Run,
    WorkflowChatContext? ChatContext)
{
    public static implicit operator WorkflowChatInteractionAcceptedReceipt(
        WorkflowChatRunAcceptedReceipt run) =>
        new(run, null);
}
