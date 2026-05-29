using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Google.Protobuf;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class ChatRunToolCompletionCoordinator
{
    public static IReadOnlyList<string> CompleteInvocationToolNames { get; } =
    [
        "aevatar_invoke_gagent",
        "aevatar_invoke_team",
        "aevatar_start_workflow",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly HashSet<string> CompleteInvocationToolNameSet =
        CompleteInvocationToolNames.ToHashSet(StringComparer.Ordinal);

    private readonly IChatRunActorPort _chatRunActorPort;
    private readonly IActorEventSubscriptionProvider _subscriptionProvider;
    private readonly IGAgentRunTerminalQueryPort _gagentTerminalQueryPort;
    private readonly IServiceRunQueryPort _serviceRunQueryPort;
    private readonly IWorkflowExecutionQueryApplicationService _workflowQueryService;

    // Refactor (issue1334): Old pattern: ChatRun completion coordination named folded actor results as generic ResultJson. New principle: actor-internal terminal payloads use internal_result_json and only boundary dispatch JSON remains boundary-named.
    public ChatRunToolCompletionCoordinator(
        IChatRunActorPort chatRunActorPort,
        IActorEventSubscriptionProvider subscriptionProvider,
        IGAgentRunTerminalQueryPort gagentTerminalQueryPort,
        IServiceRunQueryPort serviceRunQueryPort,
        IWorkflowExecutionQueryApplicationService workflowQueryService)
    {
        _chatRunActorPort = chatRunActorPort ?? throw new ArgumentNullException(nameof(chatRunActorPort));
        _subscriptionProvider = subscriptionProvider ?? throw new ArgumentNullException(nameof(subscriptionProvider));
        _gagentTerminalQueryPort = gagentTerminalQueryPort ?? throw new ArgumentNullException(nameof(gagentTerminalQueryPort));
        _serviceRunQueryPort = serviceRunQueryPort ?? throw new ArgumentNullException(nameof(serviceRunQueryPort));
        _workflowQueryService = workflowQueryService ?? throw new ArgumentNullException(nameof(workflowQueryService));
    }

    public static bool IsWaitCompleteInvocationTool(ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        if (!CompleteInvocationToolNameSet.Contains(toolCall.Name))
            return false;

        return TryReadWait(toolCall.ArgumentsJson, out var wait) &&
               string.Equals(wait, "complete", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> ExecuteAsync(
        LLMRequest request,
        ToolCall toolCall,
        string argumentsJson,
        Func<ChatRunToolCompletionRequest, CancellationToken, Task<ChatRunToolCompletionRequest>> executeToolAsync,
        int llmRound,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(executeToolAsync);

        var responseId = ResolveResponseId(request);
        var actorId = await _chatRunActorPort.StartAsync(
            new ChatRunStartRequest(
                responseId,
                request.Model,
                request.Messages),
            ct);

        var readySource = new TaskCompletionSource<ChatRunToolResultReady>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await _subscriptionProvider.SubscribeAsync<ChatRunToolResultReady>(
            actorId,
            ready =>
            {
                if (string.Equals(ready.CallerToolCallId, toolCall.Id, StringComparison.Ordinal))
                    readySource.TrySetResult(ready.Clone());
                return Task.CompletedTask;
            },
            ct);

        var isGAgentInvocation = string.Equals(toolCall.Name, "aevatar_invoke_gagent", StringComparison.Ordinal);
        var hasInlineGAgentActorId = TryReadString(argumentsJson, "actor_id", out _);
        if (isGAgentInvocation)
        {
            await _chatRunActorPort.BeginSubRunObservationAsync(
                actorId,
                new ChatRunToolCompletionRequest(
                    responseId,
                    request.Model,
                    request.Messages,
                    toolCall,
                    argumentsJson,
                    string.Empty,
                    llmRound),
                ct);
        }

        var initialRequest = new ChatRunToolCompletionRequest(
            responseId,
            request.Model,
            request.Messages,
            toolCall,
            argumentsJson,
            string.Empty,
            llmRound);
        var completionRequest = await executeToolAsync(initialRequest, ct);
        var dispatchResult = completionRequest.ToolExecutionResultJson ?? string.Empty;
        await _chatRunActorPort.SubmitToolCallAsync(actorId, completionRequest, ct);

        if (!string.IsNullOrWhiteSpace(completionRequest.ErrorCode))
            return dispatchResult;

        completionRequest = NormalizeDispatchResultForObservation(toolCall, argumentsJson, completionRequest);
        if (!isGAgentInvocation || !hasInlineGAgentActorId)
            await _chatRunActorPort.BeginSubRunObservationAsync(actorId, completionRequest, ct);

        var terminal = TryBuildTerminalFromDispatchResult(completionRequest) ??
                       await ResolveTerminalAsync(toolCall.Name, completionRequest, ct);
        if (terminal == null && isGAgentInvocation)
        {
            var observed = await readySource.Task.WaitAsync(ct);
            return observed.InternalResultJson;
        }

        terminal ??= BuildTerminal(
            completionRequest,
            "completion_not_observed",
            BuildCompletionNotObservedResult(toolCall.Name, completionRequest),
            completionObserved: false);
        if (string.IsNullOrWhiteSpace(terminal.RunId))
            return terminal.InternalResultJson;

        await _chatRunActorPort.ObserveSubRunTerminalAsync(actorId, terminal, ct);

        return (await readySource.Task.WaitAsync(ct)).InternalResultJson;
    }

    // Refactor (iter290/cluster001): Old pattern: observation targets were recovered from tool ResultJson after dispatch. New principle: observation target fields are normalized before entering actor observation.
    private static ChatRunToolCompletionRequest NormalizeDispatchResultForObservation(
        ToolCall toolCall,
        string argumentsJson,
        ChatRunToolCompletionRequest request)
    {
        if (string.Equals(toolCall.Name, "aevatar_invoke_gagent", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(request.ActorId) &&
            TryReadString(argumentsJson, "actor_id", out var actorId))
        {
            return request with { ActorId = actorId };
        }

        return request;
    }

    private async Task<ChatRunSubRunTerminalObserved?> ResolveTerminalAsync(
        string toolName,
        ChatRunToolCompletionRequest dispatch,
        CancellationToken ct)
    {
        if (string.Equals(toolName, "aevatar_invoke_gagent", StringComparison.Ordinal))
            return await ResolveGAgentTerminalAsync(dispatch, ct);

        if (string.Equals(toolName, "aevatar_invoke_team", StringComparison.Ordinal))
            return await ResolveTeamTerminalAsync(dispatch, ct);

        if (string.Equals(toolName, "aevatar_start_workflow", StringComparison.Ordinal))
            return await ResolveWorkflowTerminalAsync(dispatch, ct);

        return null;
    }

    private async Task<ChatRunSubRunTerminalObserved?> ResolveGAgentTerminalAsync(
        ChatRunToolCompletionRequest dispatch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ActorId) && !string.IsNullOrWhiteSpace(dispatch.RunId))
        {
            var snapshot = await _gagentTerminalQueryPort.GetByCorrelationIdAsync(
                dispatch.ActorId,
                dispatch.RunId,
                ct);
            if (snapshot != null && snapshot.Status != GAgentRunTerminalStatus.Unknown)
                return BuildTerminal(dispatch, snapshot.Status.ToString(), ToJson(snapshot), completionObserved: true);
        }

        return null;
    }

    private async Task<ChatRunSubRunTerminalObserved?> ResolveTeamTerminalAsync(
        ChatRunToolCompletionRequest dispatch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ServiceId) && !string.IsNullOrWhiteSpace(dispatch.RunId))
        {
            var snapshot = await _serviceRunQueryPort.GetByRunIdAsync(
                dispatch.ScopeId,
                dispatch.ServiceId,
                dispatch.RunId,
                ct);
            if (snapshot != null && IsTerminalServiceRunStatus(snapshot.Status))
                return BuildTerminal(dispatch, snapshot.Status.ToString(), ToJson(snapshot), completionObserved: true);
        }

        return null;
    }

    private async Task<ChatRunSubRunTerminalObserved?> ResolveWorkflowTerminalAsync(
        ChatRunToolCompletionRequest dispatch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ActorId))
        {
            var snapshot = await _workflowQueryService.GetWorkflowActorCurrentStateAsync(dispatch.ActorId, ct);
            if (snapshot != null &&
                string.Equals(snapshot.LastCommandId, dispatch.RunId, StringComparison.Ordinal) &&
                IsTerminalWorkflowStatus(snapshot.CompletionStatus))
            {
                return BuildTerminal(dispatch, snapshot.CompletionStatus.ToString(), ProtoJson(snapshot), completionObserved: true);
            }
        }

        return null;
    }

    private static ChatRunSubRunTerminalObserved? TryBuildTerminalFromDispatchResult(ChatRunToolCompletionRequest dispatch)
    {
        if (string.IsNullOrWhiteSpace(dispatch.RunId) ||
            string.IsNullOrWhiteSpace(dispatch.CompletionResultJson) ||
            !IsTerminalDispatchStatus(dispatch.Status) ||
            !dispatch.CompletionObserved)
        {
            return null;
        }

        return BuildTerminal(dispatch, dispatch.Status, dispatch.CompletionResultJson, completionObserved: true);
    }

    // Refactor (iter290/cluster001): Old pattern: terminal observation rebuilt control facts from ResultJson. New principle: terminal observation copies typed dispatch fields into the actor event.
    private static ChatRunSubRunTerminalObserved BuildTerminal(
        ChatRunToolCompletionRequest dispatch,
        string status,
        string resultJson,
        bool completionObserved) =>
        new()
        {
            RunId = dispatch.RunId,
            Status = status,
            InternalResultJson = resultJson,
            ActorId = dispatch.ActorId,
            ServiceId = dispatch.ServiceId,
            EndpointId = dispatch.EndpointId,
            CompletionObserved = completionObserved,
            ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private static string ResolveResponseId(LLMRequest request) =>
        request.CallerContext?.ResponseId?.Trim()
        ?? request.RequestId?.Trim()
        ?? Guid.NewGuid().ToString("N");

    private static bool TryReadWait(
        string argumentsJson,
        out string wait)
    {
        wait = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            wait = ReadString(root, "wait");
            return !string.IsNullOrWhiteSpace(wait);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTerminalDispatchStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return !status.Equals("accepted", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("streaming", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("running", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalServiceRunStatus(ServiceRunStatus status) =>
        status is ServiceRunStatus.Completed or ServiceRunStatus.Failed or ServiceRunStatus.Stopped;

    private static bool IsTerminalWorkflowStatus(WorkflowRunCompletionStatus status) =>
        status is WorkflowRunCompletionStatus.Completed
            or WorkflowRunCompletionStatus.TimedOut
            or WorkflowRunCompletionStatus.Failed
            or WorkflowRunCompletionStatus.Stopped
            or WorkflowRunCompletionStatus.NotFound
            or WorkflowRunCompletionStatus.Disabled;

    private static string BuildCompletionNotObservedResult(string toolName, ChatRunToolCompletionRequest dispatch) =>
        ToJson(new
        {
            run_id = EmptyToNull(dispatch.RunId),
            status = EmptyToNull(dispatch.Status),
            stream_topic = EmptyToNull(dispatch.StreamTopic),
            actor_id = EmptyToNull(dispatch.ActorId),
            service_id = EmptyToNull(dispatch.ServiceId),
            endpoint_id = EmptyToNull(dispatch.EndpointId),
            wait = "complete",
            error = new
            {
                code = "completion_not_observed",
                message = $"{toolName} wait=complete did not observe a terminal result for this run.",
            },
        });

    private static bool TryReadString(string json, string propertyName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            value = ReadString(document.RootElement, propertyName);
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string ToJson<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static string ProtoJson(IMessage message) =>
        JsonFormatter.Default.Format(message);

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
