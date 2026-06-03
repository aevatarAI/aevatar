using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Host endpoints reconstructed caller identity and origin facts while also owning response/message orchestration.
//   New principle: Application receives a typed caller scope so command facades can validate visibility and build LLM caller context without Host-side business branching.
public sealed record ResponsesCallerScope(
    string ScopeId,
    string OwnerSubject,
    LlmSessionOriginKind OriginKind);

// Refactor (iter159/cluster-640-first): Old: ResolveAsync(string bearer)  New: ResolveAsync(ResponsesCallerScopeResolutionContext)
//   Old pattern: caller-scope resolution received only the inbound bearer string, so NyxID proxy assertions could not travel through the shared admission seam.
//   New principle: Host adapters carry all inbound caller evidence as typed Application input while the resolver decides the authoritative scope.
public sealed record ResponsesCallerScopeResolutionContext(
    string InboundBearerToken,
    string? NyxIdIdentityToken,
    string? NyxIdDelegationToken);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Each endpoint resolved NyxID bearer tokens directly before continuing its inline command flow.
//   New principle: Token-to-caller-scope resolution is a narrow Application port; Host composes the concrete adapter and command facades consume the typed result.
public interface IResponsesCallerScopeResolver
{
    Task<ResponsesCallerScope> ResolveAsync(
        ResponsesCallerScopeResolutionContext context,
        CancellationToken ct = default);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Authentication failures escaped from endpoint-local scope resolution as boundary exceptions mixed into HTTP handler code.
//   New principle: Scope resolution has one typed failure signal that facades map into protocol-specific command errors.
public sealed class ResponsesCallerScopeUnavailableException : Exception
{
    public ResponsesCallerScopeUnavailableException(string message) : base(message)
    {
    }
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Host endpoints parsed catalog route slugs and resolved route values while constructing provider requests.
//   New principle: Application depends on a route-value port and owns model-route command context; Host only wires the boundary implementation.
public interface IResponsesRouteResolver
{
    Task<string?> ResolveRouteValueAsync(
        string slug,
        string bearerToken,
        CancellationToken ct);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Responses/Messages Application facades constructed chat route snapshots with the concrete ChatRouting.Core resolver.
//   New principle: Application depends on a business route-decision port; Host composes that port with the current readmodel query and resolver implementation.
public interface IResponsesChatRouteDecisionPort
{
    Task<ChatRouteDecision> ResolveAsync(
        ResponsesCallerScope callerScope,
        string model,
        ToolMode toolMode,
        string contentHint,
        CancellationToken ct = default);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Host API models were passed through deep endpoint handlers and normalized beside session registration and LLM execution.
//   New principle: Host maps external JSON into typed command requests; Application normalizes and executes the command lifecycle.
public sealed record ResponsesCommandRequest(
    string? Model,
    string? Prompt,
    IReadOnlyList<ResponsesToolResultInput> ToolResults,
    bool? Stream,
    string? PreviousResponseId,
    double? Temperature,
    int? MaxOutputTokens,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Anthropic Messages HTTP payloads were normalized inside the Minimal API handler.
//   New principle: Host passes a typed command request and Application owns Messages-specific validation plus LLM request construction.
public sealed record MessagesCommandRequest(
    string? Model,
    int? MaxTokens,
    IReadOnlyList<ChatMessage> ChatMessages,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools,
    bool DroppedImageContent,
    double? Temperature,
    double? TopP,
    int? TopK,
    IReadOnlyList<string>? StopSequences,
    bool? Stream,
    bool ToolChoiceDisablesTools,
    string? ToolChoiceError);

// Refactor (iter344/cluster-001):
//   Old pattern: Host handler owns caller resolution, route resolution, session registration, tool planning, direct provider execution, status updates, and protocol rendering in one request stack.
//   New principle: Host maps HTTP/OpenAI frames only; typed Application facade owns Normalize -> Resolve Target -> Build Context -> Build Envelope -> Dispatch -> Receipt/Observe via the same LlmSessionGAgent run path as Responses/Messages.
public sealed record ChatCompletionsCommandRequest(
    string? Model,
    bool? Stream,
    bool IncludeUsageInStream,
    double? Temperature,
    int? MaxTokens,
    IReadOnlyList<ChatMessage> ChatMessages,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools,
    LLMResponseFormat? ResponseFormat);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Normalized Responses fields lived as endpoint locals across routing, continuation, session, and execution branches.
//   New principle: Normalized command state is an immutable Application value passed through the facade lifecycle.
public sealed record NormalizedResponsesRequest(
    string ResponseId,
    string MessageItemId,
    string Model,
    string Prompt,
    bool Stream,
    string? PreviousResponseId,
    double? Temperature,
    int? MaxOutputTokens,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools,
    IReadOnlyList<ResponsesToolResultInput> ToolResults);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Function-call output fields were unpacked ad hoc in endpoint continuation code.
//   New principle: Tool result inputs are typed command data so continuation validation and persistence stay in Application.
public sealed record ResponsesToolResultInput(
    string CallId,
    string Output,
    string? SchemaHash);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Normalization failures returned HTTP results directly from endpoint-local validation branches.
//   New principle: Normalizers return typed success/failure values that command facades and Host mappers can translate honestly.
public readonly record struct ResponsesRequestNormalizationResult(
    NormalizedResponsesRequest? Request,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Request != null && ErrorCode == null;

    public static ResponsesRequestNormalizationResult Success(NormalizedResponsesRequest request) =>
        new(request, null, null);

    public static ResponsesRequestNormalizationResult Failed(string code, string message) =>
        new(null, code, message);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Messages execution carried normalized locals through the Host handler.
//   New principle: Messages command state is a typed Application value shared by streaming and non-streaming execution paths.
public sealed record NormalizedMessagesRequest(
    string MessageId,
    string Model,
    int MaxTokens,
    bool Stream,
    double? Temperature,
    IReadOnlyList<ChatMessage> ChatMessages,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools,
    bool DroppedImageContent);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Messages validation failures were protocol responses produced inside the Host handler.
//   New principle: Messages normalization returns typed failure data for the command facade to map without owning HTTP concerns.
public readonly record struct MessagesRequestNormalizationResult(
    NormalizedMessagesRequest? Request,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Request != null && ErrorCode == null;

    public static MessagesRequestNormalizationResult Success(NormalizedMessagesRequest request) =>
        new(request, null, null);

    public static MessagesRequestNormalizationResult Failed(string code, string message) =>
        new(null, code, message);
}

// Refactor (iter344/cluster-001):
//   Old pattern: Chat Completions normalized state lived as Host locals across route/session/tool/provider branches.
//   New principle: Application carries a typed normalized command state through route resolution, envelope construction, and dispatch.
public sealed record NormalizedChatCompletionsCommand(
    string CompletionId,
    string Model,
    bool Stream,
    bool IncludeUsageInStream,
    double? Temperature,
    int? MaxTokens,
    IReadOnlyList<ChatMessage> ChatMessages,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools,
    LLMResponseFormat? ResponseFormat);

// Refactor (iter344/cluster-001):
//   Old pattern: Chat Completions validation failures returned HTTP results from endpoint branches.
//   New principle: Application normalization returns typed success/failure data for Host protocol rendering.
public readonly record struct ChatCompletionsRequestNormalizationResult(
    NormalizedChatCompletionsCommand? Request,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Request != null && ErrorCode == null;

    public static ChatCompletionsRequestNormalizationResult Success(NormalizedChatCompletionsCommand request) =>
        new(request, null, null);

    public static ChatCompletionsRequestNormalizationResult Failed(string code, string message) =>
        new(null, code, message);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Error status/code/message triples were repeatedly assembled in Host branches.
//   New principle: Application command results carry typed errors; Host performs only protocol rendering.
public sealed record ResponsesCommandError(
    int StatusCode,
    string Code,
    string Message);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Streaming execution captured endpoint locals after session registration.
//   New principle: A stream plan is the accepted command context that Host can render as SSE while Application still owns execution.
public sealed record ResponsesCreateCommandPlan(
    NormalizedResponsesRequest Normalized,
    LlmSessionRegistrationResult Session,
    LlmSessionSnapshot? PreviousSnapshot,
    LLMRequest LlmRequest,
    AgentToolExecutionContext ToolContext,
    ResponsesToolClassification ToolClassification,
    ResponsesToolChoiceHintPlan ToolChoiceHintPlan,
    DateTimeOffset CreatedAt);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Create response branches returned HTTP/SSE/JSON directly from orchestration code.
//   New principle: Application returns one typed union for error, stream plan, or completed result.
public sealed record ResponsesCreateCommandResult(
    ResponsesCommandError? Error,
    ResponsesCreateCommandPlan? StreamPlan,
    ResponsesCreateCompletedCommandResult? Completed,
    ResponsesCreateAcceptedCommandResult? Accepted)
{
    public static ResponsesCreateCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null, null, null);

    public static ResponsesCreateCommandResult FromStreamPlan(ResponsesCreateCommandPlan plan) =>
        new(null, plan, null, null);

    public static ResponsesCreateCommandResult FromCompleted(ResponsesCreateCompletedCommandResult completed) =>
        new(null, null, completed, null);

    public static ResponsesCreateCommandResult FromAccepted(ResponsesCreateAcceptedCommandResult accepted) =>
        new(null, null, null, accepted);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Completed Responses JSON shape was built from endpoint execution locals.
//   New principle: Application exposes the completed command data; Host maps it to the external Responses protocol.
public sealed record ResponsesCreateCompletedCommandResult(
    NormalizedResponsesRequest Normalized,
    long CreatedAt,
    LlmSessionCompletionSnapshot Completion);

// Refactor (iter103/cluster-1 r2):
//   Old pattern: Application facades treated IActorDispatchPort ACK as committed/readmodel-observed completion.
//   New principle: direct Responses/Messages create returns only the accepted dispatch receipt; terminal completion is observed asynchronously.
public sealed record ResponsesCreateAcceptedCommandResult(
    NormalizedResponsesRequest Normalized,
    long CreatedAt,
    LlmSessionRegistrationResult Session,
    DispatchAdmission Admission);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Streaming completion errors and final data were encoded directly into SSE handler branches.
//   New principle: Application reports stream execution outcome as typed data; Host renders the appropriate SSE or error frame.
public sealed record ResponsesStreamCommandResult(
    ResponsesCommandError? Error,
    LlmSessionCompletionSnapshot? Completion,
    ResponsesStreamAcceptedCommandResult? Accepted)
{
    public static ResponsesStreamCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null, null);

    public static ResponsesStreamCommandResult FromCompleted(
        LlmSessionCompletionSnapshot completion) =>
        new(null, completion, null);

    public static ResponsesStreamCommandResult FromAccepted(ResponsesStreamAcceptedCommandResult accepted) =>
        new(null, null, accepted);
}

public sealed record ResponsesStreamAcceptedCommandResult(
    DispatchAdmission Admission);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Cancel response visibility and status transition lived inside the Host endpoint.
//   New principle: Cancellation is an Application command result; Host only validates the route id and renders the protocol response.
public sealed record ResponsesCancelCommandResult(
    ResponsesCommandError? Error,
    string? ResponseId,
    long? CancelledAt)
{
    public static ResponsesCancelCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null, null);

    public static ResponsesCancelCommandResult FromCancelled(string responseId, long cancelledAt) =>
        new(null, responseId, cancelledAt);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Messages streaming used endpoint locals after registering the LLM session.
//   New principle: Application returns a typed Messages stream plan and Host owns only Anthropic SSE frame rendering.
public sealed record MessagesCreateCommandPlan(
    NormalizedMessagesRequest Normalized,
    LlmSessionRegistrationResult Session,
    LLMRequest LlmRequest,
    AgentToolExecutionContext ToolContext,
    ResponsesToolClassification ToolClassification,
    ResponsesToolChoiceHintPlan ToolChoiceHintPlan);

// Refactor (iter344/cluster-001):
//   Old pattern: Chat Completions streaming held prepared LLMRequest/session/tool state inside the Host SSE helper.
//   New principle: Application returns a typed run plan; Host only renders OpenAI-compatible event frames around dispatch outcome.
public sealed record ChatCompletionsCreateCommandPlan(
    NormalizedChatCompletionsCommand Normalized,
    LlmSessionRegistrationResult Session,
    LLMRequest LlmRequest,
    ResponsesToolClassification ToolClassification,
    ResponsesToolChoiceHintPlan ToolChoiceHintPlan,
    DateTimeOffset CreatedAt);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Messages create execution directly selected HTTP JSON versus SSE in the Host orchestration body.
//   New principle: Application returns a typed result that separates validation errors, stream plans, and completed content.
public sealed record MessagesCreateCommandResult(
    ResponsesCommandError? Error,
    MessagesCreateCommandPlan? StreamPlan,
    MessagesCreateCompletedCommandResult? Completed,
    MessagesCreateAcceptedCommandResult? Accepted)
{
    public static MessagesCreateCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null, null, null);

    public static MessagesCreateCommandResult FromStreamPlan(MessagesCreateCommandPlan plan) =>
        new(null, plan, null, null);

    public static MessagesCreateCommandResult FromCompleted(MessagesCreateCompletedCommandResult completed) =>
        new(null, null, completed, null);

    public static MessagesCreateCommandResult FromAccepted(MessagesCreateAcceptedCommandResult accepted) =>
        new(null, null, null, accepted);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Completed Messages payload data stayed coupled to Host response construction.
//   New principle: Application exposes the completed Messages command outcome for the Host protocol mapper.
public sealed record MessagesCreateCompletedCommandResult(
    NormalizedMessagesRequest Normalized,
    LlmSessionCompletionSnapshot Completion);

public sealed record MessagesCreateAcceptedCommandResult(
    NormalizedMessagesRequest Normalized,
    LlmSessionRegistrationResult Session,
    DispatchAdmission Admission);

// Refactor (iter344/cluster-001):
//   Old pattern: Chat Completions create branched directly in Host between request-local execution and SSE output.
//   New principle: Application returns one typed union for protocol error, stream plan, or accepted dispatch receipt.
public sealed record ChatCompletionsCreateCommandResult(
    ResponsesCommandError? Error,
    ChatCompletionsCreateCommandPlan? StreamPlan,
    ChatCompletionsCreateAcceptedCommandResult? Accepted)
{
    public static ChatCompletionsCreateCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null, null);

    public static ChatCompletionsCreateCommandResult FromStreamPlan(ChatCompletionsCreateCommandPlan plan) =>
        new(null, plan, null);

    public static ChatCompletionsCreateCommandResult FromAccepted(ChatCompletionsCreateAcceptedCommandResult accepted) =>
        new(null, null, accepted);
}

// Refactor (iter344/cluster-001):
//   Old pattern: The synchronous Chat Completions response implied request-local completion from direct provider execution.
//   New principle: The create response exposes only the accepted actor dispatch receipt; terminal completion is observed asynchronously.
public sealed record ChatCompletionsCreateAcceptedCommandResult(
    NormalizedChatCompletionsCommand Normalized,
    long CreatedAt,
    LlmSessionRegistrationResult Session,
    DispatchAdmission Admission);

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: /v1/responses endpoints owned command orchestration and called many lower-level collaborators directly.
//   New principle: Host depends on one typed Application command facade for create/cancel/stream operations.
public interface IResponsesCommandFacade
{
    Task<ResponsesCreateCommandResult> CreateAsync(
        ResponsesCommandRequest request,
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default);

    Task<ResponsesCancelCommandResult> CancelAsync(
        string responseId,
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default);

    Task<ResponsesStreamCommandResult> StreamAsync(
        ResponsesCreateCommandPlan plan,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken ct = default);
}

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: /v1/messages endpoints duplicated Responses command orchestration for the Anthropic protocol shape.
//   New principle: Host depends on a Messages-specific Application command facade that shares typed command contracts and execution ports.
public interface IMessagesCommandFacade
{
    Task<MessagesCreateCommandResult> CreateAsync(
        MessagesCommandRequest request,
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default);

    Task<ResponsesStreamCommandResult> StreamAsync(
        MessagesCreateCommandPlan plan,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken ct = default);
}

// Refactor (iter344/cluster-001):
//   Old pattern: /v1/chat/completions endpoint injected route/session/tool/provider collaborators and executed the LLM loop directly.
//   New principle: Host depends on one Chat Completions Application facade that owns the command lifecycle and actor dispatch.
public interface IChatCompletionsCommandFacade
{
    Task<ChatCompletionsCreateCommandResult> CreateAsync(
        ChatCompletionsCommandRequest request,
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default);

    Task<ResponsesStreamCommandResult> StreamAsync(
        ChatCompletionsCreateCommandPlan plan,
        CancellationToken ct = default);
}
