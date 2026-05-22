using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Responses;

public sealed record ResponsesCallerScope(
    string ScopeId,
    string OwnerSubject,
    LlmSessionOriginKind OriginKind);

public interface IResponsesCallerScopeResolver
{
    Task<ResponsesCallerScope> ResolveAsync(
        string nyxIdAccessToken,
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

public sealed record ResponsesCommandError(
    int StatusCode,
    string Code,
    string Message);

public sealed record ResponsesCreateCommandPlan(
    NormalizedResponsesRequest Normalized,
    LlmSessionRegistrationResult Session,
    LlmSessionSnapshot? PreviousSnapshot,
    LLMRequest LlmRequest,
    IReadOnlyDictionary<string, string> ToolContextMetadata,
    ResponsesToolClassification ToolClassification,
    DateTimeOffset CreatedAt);

public sealed record ResponsesCreateCommandResult(
    ResponsesCommandError? Error,
    ResponsesCreateCommandPlan? StreamPlan,
    ResponsesCreateCompletedCommandResult? Completed,
    ResponsesForwardCommandResult? Forward)
{
    public static ResponsesCreateCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null, null, null);

    public static ResponsesCreateCommandResult FromStreamPlan(ResponsesCreateCommandPlan plan) =>
        new(null, plan, null, null);

    public static ResponsesCreateCommandResult FromCompleted(ResponsesCreateCompletedCommandResult completed) =>
        new(null, null, completed, null);

    public static ResponsesCreateCommandResult FromForward(ResponsesForwardCommandResult forward) =>
        new(null, null, null, forward);
}

public sealed record ResponsesForwardCommandResult(
    NormalizedResponsesRequest Normalized,
    ResponsesCallerScope CallerScope,
    ChatRouteAction Action);

public sealed record ResponsesCreateCompletedCommandResult(
    NormalizedResponsesRequest Normalized,
    long CreatedAt,
    long CompletedAt,
    string OutputText,
    IReadOnlyList<ToolCall> ForwardedToolCalls,
    TokenUsage? Usage);

public sealed record ResponsesStreamCommandResult(
    ResponsesCommandError? Error,
    string OutputText,
    IReadOnlyList<ToolCall> ForwardedToolCalls,
    TokenUsage? Usage)
{
    public static ResponsesStreamCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), string.Empty, [], null);

    public static ResponsesStreamCommandResult FromCompleted(
        string outputText,
        IReadOnlyList<ToolCall> forwardedToolCalls,
        TokenUsage? usage) =>
        new(null, outputText, forwardedToolCalls, usage);
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
    IReadOnlyDictionary<string, string> ToolContextMetadata,
    ResponsesToolClassification ToolClassification);

public sealed record MessagesCreateCommandResult(
    ResponsesCommandError? Error,
    MessagesCreateCommandPlan? StreamPlan,
    MessagesCreateCompletedCommandResult? Completed)
{
    public static MessagesCreateCommandResult FromError(int statusCode, string code, string message) =>
        new(new ResponsesCommandError(statusCode, code, message), null, null);

    public static MessagesCreateCommandResult FromStreamPlan(MessagesCreateCommandPlan plan) =>
        new(null, plan, null);

    public static MessagesCreateCommandResult FromCompleted(MessagesCreateCompletedCommandResult completed) =>
        new(null, null, completed);
}

public sealed record MessagesCreateCompletedCommandResult(
    NormalizedMessagesRequest Normalized,
    ResponsesCompletionResult Completion);

public interface IResponsesCommandFacade
{
    Task<ResponsesCreateCommandResult> CreateAsync(
        ResponsesCommandRequest request,
        string bearerToken,
        CancellationToken ct = default);

    Task<ResponsesCancelCommandResult> CancelAsync(
        string responseId,
        string bearerToken,
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
        string bearerToken,
        CancellationToken ct = default);

    Task<ResponsesStreamCommandResult> StreamAsync(
        MessagesCreateCommandPlan plan,
        Func<string, CancellationToken, ValueTask> onTextDelta,
        CancellationToken ct = default);
}
