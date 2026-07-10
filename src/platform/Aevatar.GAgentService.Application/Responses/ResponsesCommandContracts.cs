using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Responses;

public sealed record ResponsesCallerScope(
    string ScopeId,
    string OwnerSubject,
    LlmSessionOriginKind OriginKind);

public sealed record ResponsesCallerScopeResolutionContext(
    string InboundBearerToken,
    string? NyxIdIdentityToken,
    string? NyxIdDelegationToken);

public interface IResponsesCallerScopeResolver
{
    Task<ResponsesCallerScope> ResolveAsync(
        ResponsesCallerScopeResolutionContext context,
        CancellationToken ct = default);
}

public sealed class ResponsesCallerScopeUnavailableException : Exception
{
    public ResponsesCallerScopeUnavailableException(string message) : base(message)
    {
    }
}

public interface IResponsesRouteResolver
{
    Task<string?> ResolveRouteValueAsync(
        string slug,
        string bearerToken,
        CancellationToken ct);
}

public sealed record ResponsesChatRouteDecisionRequest(
    ResponsesCallerScope CallerScope,
    string Model,
    ToolMode ToolMode,
    string ContentHint,
    string CommandName);

public interface IResponsesChatRouteDecisionPort
{
    Task<ChatRouteDecision> ResolveAsync(
        ResponsesChatRouteDecisionRequest request,
        CancellationToken ct = default);
}

public sealed record ResponsesCommandRequest(
    string? Model,
    string? Prompt,
    IReadOnlyList<ResponsesToolResultInput> ToolResults,
    bool? Stream,
    string? PreviousResponseId,
    double? Temperature,
    int? MaxOutputTokens,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools);

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

public sealed record ChatCompletionsCommandRequest(
    string? Model,
    bool? Stream,
    bool IncludeUsageInStream,
    double? Temperature,
    int? MaxTokens,
    IReadOnlyList<ChatMessage> ChatMessages,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools,
    LLMResponseFormat? ResponseFormat);

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

public sealed record ResponsesToolResultInput(
    string CallId,
    string Output,
    string? SchemaHash);

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

public sealed record NormalizedMessagesRequest(
    string MessageId,
    string Model,
    int MaxTokens,
    bool Stream,
    double? Temperature,
    IReadOnlyList<ChatMessage> ChatMessages,
    IReadOnlyList<ResponsesApplicationToolDeclaration> DeclaredTools,
    bool DroppedImageContent);

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

public sealed record ResponsesCommandError(
    int StatusCode,
    string Code,
    string Message);

public sealed record ResponsesCreateCommandPlan(
    NormalizedResponsesRequest Normalized,
    LlmSessionRegistrationResult Session,
    LlmSessionSnapshot? PreviousSnapshot,
    LLMRequest LlmRequest,
    AgentToolExecutionContext ToolContext,
    ResponsesToolClassification ToolClassification,
    ResponsesToolChoiceHintPlan ToolChoiceHintPlan,
    DateTimeOffset CreatedAt,
    string ResolvedToolSetName = "");

public sealed record ResponsesCreateCommandResult(
    ResponsesCommandError? Error,
    ResponsesCreateCommandPlan? StreamPlan,
    ResponsesCreateCompletedCommandResult? Completed)
{
    public static ResponsesCreateCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null, null);

    public static ResponsesCreateCommandResult FromStreamPlan(ResponsesCreateCommandPlan plan) =>
        new(null, plan, null);

    public static ResponsesCreateCommandResult FromCompleted(ResponsesCreateCompletedCommandResult completed) =>
        new(null, null, completed);
}

public sealed record ResponsesCreateCompletedCommandResult(
    NormalizedResponsesRequest Normalized,
    long CreatedAt,
    ResponsesCompletionStage CompletionStage,
    LlmSessionCompletionSnapshot Completion);

public enum ResponsesCompletionStage
{
    Committed = 0,
    ReadModelObserved = 1,
}

public sealed record ResponsesStreamCommandResult(
    ResponsesCommandError? Error,
    LlmSessionCompletionSnapshot? Completion)
{
    public static ResponsesStreamCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null);

    public static ResponsesStreamCommandResult FromCompleted(
        LlmSessionCompletionSnapshot completion) =>
        new(null, completion);
}

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

public sealed record MessagesCreateCommandPlan(
    NormalizedMessagesRequest Normalized,
    LlmSessionRegistrationResult Session,
    LLMRequest LlmRequest,
    AgentToolExecutionContext ToolContext,
    ResponsesToolClassification ToolClassification,
    ResponsesToolChoiceHintPlan ToolChoiceHintPlan,
    string ResolvedToolSetName = "");

public sealed record ChatCompletionsCreateCommandPlan(
    NormalizedChatCompletionsCommand Normalized,
    LlmSessionRegistrationResult Session,
    LLMRequest LlmRequest,
    ResponsesToolClassification ToolClassification,
    ResponsesToolChoiceHintPlan ToolChoiceHintPlan,
    DateTimeOffset CreatedAt,
    string ResolvedToolSetName = "");

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

public sealed record MessagesCreateCompletedCommandResult(
    NormalizedMessagesRequest Normalized,
    LlmSessionCompletionSnapshot Completion);

public sealed record MessagesCreateAcceptedCommandResult(
    NormalizedMessagesRequest Normalized,
    LlmSessionRegistrationResult Session,
    DispatchAdmission Admission);

public sealed record ChatCompletionsCreateCommandResult(
    ResponsesCommandError? Error,
    ChatCompletionsCreateCommandPlan? StreamPlan,
    ChatCompletionsCreateCompletedCommandResult? Completed,
    ChatCompletionsCreateAcceptedCommandResult? Accepted)
{
    public static ChatCompletionsCreateCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null, null, null);

    public static ChatCompletionsCreateCommandResult FromStreamPlan(ChatCompletionsCreateCommandPlan plan) =>
        new(null, plan, null, null);

    public static ChatCompletionsCreateCommandResult FromCompleted(ChatCompletionsCreateCompletedCommandResult completed) =>
        new(null, null, completed, null);

    public static ChatCompletionsCreateCommandResult FromAccepted(ChatCompletionsCreateAcceptedCommandResult accepted) =>
        new(null, null, null, accepted);
}

public sealed record ChatCompletionsCreateCompletedCommandResult(
    NormalizedChatCompletionsCommand Normalized,
    long CreatedAt,
    LlmSessionCompletionSnapshot Completion);

public sealed record ChatCompletionsCreateAcceptedCommandResult(
    NormalizedChatCompletionsCommand Normalized,
    long CreatedAt,
    LlmSessionRegistrationResult Session,
    DispatchAdmission Admission);

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

public interface IChatCompletionsCommandFacade
{
    Task<ChatCompletionsCreateCommandResult> CreateAsync(
        ChatCompletionsCommandRequest request,
        ResponsesCallerScopeResolutionContext callerScopeContext,
        CancellationToken ct = default);

    Task<ResponsesStreamCommandResult> StreamAsync(
        ChatCompletionsCreateCommandPlan plan,
        Func<LlmSessionRunObservedDelta, CancellationToken, ValueTask> onObservedDelta,
        CancellationToken ct = default);
}
