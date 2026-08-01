using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatCapabilityMessageTypes
{
    public const string ChatCommand = "chat.command";
}

// Refactor (phase9/cluster-349):
//   Old pattern: public chat input duplicated actor authority through top-level agentId and source actorId aliases.
//   New principle: actor targeting is owned only by typed source variant submessages; deleted aliases are rejected at the JSON boundary.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatInput
{
    /// <summary>User prompt for this chat run.</summary>
    public string? Prompt { get; init; }

    /// <summary>Structured multimodal input parts for this chat run.</summary>
    public IReadOnlyList<ChatInputContentPart>? InputParts { get; init; }

    public WorkflowChatSourceInput? Source { get; init; }

    /// <summary>Legacy workflow identifier lookup. Prefer <see cref="Source"/>.</summary>
    public string? Workflow { get; init; }

    /// <summary>
    /// Optional client-controlled session identifier for downstream chat correlation.
    /// When omitted, the server correlation id becomes the chat session id.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Legacy single inline workflow YAML field.
    /// Prefer <see cref="WorkflowYamls"/> for new clients.
    /// </summary>
    public string? WorkflowYaml { get; init; }

    /// <summary>
    /// Inline workflow YAML bundle for this request.
    /// The first item is treated as the entry workflow.
    /// If present, this field is the execution source; when <see cref="Workflow"/> is also provided,
    /// the entry workflow name must still match that lookup value.
    /// </summary>
    public IReadOnlyList<string>? WorkflowYamls { get; init; }

    /// <summary>
    /// Optional workflow scope identifier.
    /// </summary>
    // Refactor (iter15/cluster-029):
    //   Old pattern: scope id / channel facts fell back to metadata bag string keys.
    //   New principle: stable business semantics use typed proto field; metadata bag only for genuine open extension.
    public string? ScopeId { get; init; }

    /// <summary>
    /// Optional run metadata passthrough for internal bridge integrations.
    /// </summary>
    public IDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Optional command transport headers for downstream runtime adapters.
    /// </summary>
    public IDictionary<string, string>? Headers { get; init; }

    public ChatLlmControlInput? LlmControl { get; init; }

    // Refactor (issue1332): Old pattern: workflow chat control used metadata or LlmControl only. New principle: reuse AgentToolExecutionContext as typed ToolContext without adding a workflow-specific abstraction.
    public AgentToolExecutionContext? ToolContext { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HttpChatInput
{
    /// <summary>User prompt for this HTTP chat run.</summary>
    public string? Prompt { get; init; }

    /// <summary>Structured multimodal input parts for this HTTP chat run.</summary>
    public IReadOnlyList<ChatInputContentPart>? InputParts { get; init; }

    public ChatConversationInput? Conversation { get; init; }

    /// <summary>Client-controlled idempotency identity for retryable HTTP chat create requests.</summary>
    public string? CommandId { get; init; }

    /// <summary>Ignored legacy HTTP body field; trusted caller scope owns request scope.</summary>
    public JsonElement? ScopeId { get; init; }

    public WorkflowChatSourceInput? Source { get; init; }

    /// <summary>Legacy workflow identifier lookup. Prefer <see cref="Source"/>.</summary>
    public string? Workflow { get; init; }

    /// <summary>
    /// Optional client-controlled session identifier for downstream chat correlation.
    /// This is independent from Chat History conversation identity.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Legacy single inline workflow YAML field.
    /// Prefer <see cref="WorkflowYamls"/> for new clients.
    /// </summary>
    public string? WorkflowYaml { get; init; }

    /// <summary>
    /// Inline workflow YAML bundle for this request.
    /// The first item is treated as the entry workflow.
    /// </summary>
    public IReadOnlyList<string>? WorkflowYamls { get; init; }

    /// <summary>
    /// Optional run metadata passthrough for internal bridge integrations.
    /// </summary>
    public IDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Optional command transport headers for downstream runtime adapters.
    /// </summary>
    public IDictionary<string, string>? Headers { get; init; }

    public ChatLlmControlInput? LlmControl { get; init; }

    public AgentToolExecutionContext? ToolContext { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatConversationInput
{
    public string? ConversationId { get; init; }
    public long? MinimumStateVersion { get; init; }
}

public sealed record ChatLlmControlInput
{
    public string? NyxIdAccessToken { get; init; }
    public string? NyxIdOrgToken { get; init; }
    public string? NyxIdRoutePreference { get; init; }
    public string? ModelOverride { get; init; }
    public int? MaxToolRoundsOverride { get; init; }
    public string? UserMemoryPrompt { get; init; }
}

// Refactor (phase9/cluster-349):
//   Old pattern: source.actorId acted as a flat alias whose meaning changed with source kind.
//   New principle: actor id is explicit on definitionActor or inlineBundle, and strict JSON rejects the removed flat alias.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowChatSourceInput
{
    public string? Kind { get; init; }
    public WorkflowChatCatalogNameSourceInput? CatalogName { get; init; }
    public WorkflowChatDefinitionActorSourceInput? DefinitionActor { get; init; }
    public WorkflowChatInlineYamlBundleSourceInput? InlineBundle { get; init; }

    /// <summary>Legacy source workflow name alias. Prefer the typed source variant submessages.</summary>
    public string? WorkflowName { get; init; }

    /// <summary>Legacy inline YAML bundle alias. Prefer <see cref="InlineBundle"/>.</summary>
    public IReadOnlyList<string>? WorkflowYamls { get; init; }
}

public sealed record WorkflowChatCatalogNameSourceInput
{
    public string? WorkflowName { get; init; }
}

public sealed record WorkflowChatDefinitionActorSourceInput
{
    public string? ActorId { get; init; }
    public string? WorkflowName { get; init; }
}

public sealed record WorkflowChatInlineYamlBundleSourceInput
{
    public string? EntryName { get; init; }
    public IReadOnlyList<WorkflowChatInlineYamlDocumentInput>? YamlDocuments { get; init; }
    public string? ActorId { get; init; }
}

public sealed record WorkflowChatInlineYamlDocumentInput
{
    public string? Name { get; init; }
    public string? Yaml { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatInputContentPart
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? DataBase64 { get; init; }
    public string? MediaType { get; init; }
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public ChatInputInlineFile? InlineFile { get; init; }
    public ChatInputFileRef? FileRef { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatInputInlineFile
{
    public string? DataBase64 { get; init; }
    public string? MediaType { get; init; }
    public string? Name { get; init; }
    public long? SizeBytes { get; init; }
    public string? OwnerScopeId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatInputFileRef
{
    public string? FileId { get; init; }
    public string? ArtifactId { get; init; }
    public string? SourceKind { get; init; }
    public string? SourceMessageId { get; init; }
    public string? SourceResourceKey { get; init; }
    public string? FileName { get; init; }
    public string? Uri { get; init; }
    public string? MediaType { get; init; }
    public string? Name { get; init; }
    public long? CreatedAtUnixMs { get; init; }
    public long? ExpiresAtUnixMs { get; init; }
    public string? Sha256 { get; init; }
    public string? OwnerRunId { get; init; }
    public string? OwnerScopeId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowResumeInput
{
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public required string StepId { get; init; }
    public string? CommandId { get; init; }
    public bool Approved { get; init; }
    public string? UserInput { get; init; }
    public string? EditedContent { get; init; }
    public string? Feedback { get; init; }
    public IDictionary<string, string>? Metadata { get; init; }
    public WorkflowToolApprovalResumeInput? ToolApproval { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowToolApprovalResumeInput
{
    public required string ExecutionId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ApprovalRequestId { get; init; }
}

public sealed record WorkflowSignalInput
{
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public required string SignalName { get; init; }
    public string? StepId { get; init; }
    public string? CommandId { get; init; }
    public string? Payload { get; init; }
}

public sealed record WorkflowStopInput
{
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public string? CommandId { get; init; }
    public string? Reason { get; init; }
}

public sealed record WorkflowRetryCompensationInput
{
    public required string ActorId { get; init; }
    public required string RunId { get; init; }
    public required string FailedCompensationStepId { get; init; }
    public string? CommandId { get; init; }
    public string? Reason { get; init; }
}

public sealed record WorkflowForkRunInput
{
    public required string SourceRunId { get; init; }
    public required string StartAtStepId { get; init; }
    public string? InlineYaml { get; init; }
    public IDictionary<string, string>? InlineSubYamls { get; init; }
    public IDictionary<string, string>? VariableOverrides { get; init; }
    public string? Input { get; init; }
    public string? CommandId { get; init; }
    public string? CorrelationId { get; init; }
}

internal sealed record ChatWsCommand
{
    public string Type { get; init; } = ChatCapabilityMessageTypes.ChatCommand;
    public string? RequestId { get; init; }
    public ChatInput? Payload { get; init; }
}
