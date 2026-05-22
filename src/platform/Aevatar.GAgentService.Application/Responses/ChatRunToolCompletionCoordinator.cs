using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class ChatRunToolCompletionCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly HashSet<string> CompleteInvocationToolNames = new(StringComparer.Ordinal)
    {
        "aevatar_invoke_gagent",
        "aevatar_invoke_team",
        "aevatar_start_workflow",
    };

    private readonly IChatRunActorPort _chatRunActorPort;
    private readonly IActorEventSubscriptionProvider _subscriptionProvider;
    private readonly IGAgentRunTerminalQueryPort _gagentTerminalQueryPort;
    private readonly IServiceRunQueryPort _serviceRunQueryPort;
    private readonly IWorkflowExecutionQueryApplicationService _workflowQueryService;

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
        if (!CompleteInvocationToolNames.Contains(toolCall.Name))
            return false;

        return TryReadWait(toolCall.ArgumentsJson, out var wait) &&
               string.Equals(wait, "complete", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> ExecuteAsync(
        LLMRequest request,
        ToolCall toolCall,
        string argumentsJson,
        Func<string, CancellationToken, Task<string>> executeToolAsync,
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

        if (string.Equals(toolCall.Name, "aevatar_invoke_gagent", StringComparison.Ordinal))
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

        var dispatchResult = await executeToolAsync(argumentsJson, ct);
        var completionRequest = new ChatRunToolCompletionRequest(
            responseId,
            request.Model,
            request.Messages,
            toolCall,
            argumentsJson,
            dispatchResult,
            llmRound);
        await _chatRunActorPort.SubmitToolCallAsync(actorId, completionRequest, ct);

        var parsed = ParseDispatchResult(dispatchResult);
        if (!string.IsNullOrWhiteSpace(parsed.ErrorCode))
            return dispatchResult;

        parsed = NormalizeDispatchResultForObservation(toolCall, argumentsJson, parsed);
        if (!string.Equals(toolCall.Name, "aevatar_invoke_gagent", StringComparison.Ordinal))
            await _chatRunActorPort.BeginSubRunObservationAsync(actorId, completionRequest, ct);

        var terminal = await ResolveTerminalAsync(toolCall.Name, parsed, ct);
        if (terminal == null && string.Equals(toolCall.Name, "aevatar_invoke_gagent", StringComparison.Ordinal))
        {
            var observed = await readySource.Task.WaitAsync(ct);
            return observed.ResultJson;
        }

        terminal ??= BuildTerminal(parsed, "completed", ToJson(new
        {
            run_id = parsed.RunId,
            status = "completed",
            actor_id = EmptyToNull(parsed.ActorId),
            service_id = EmptyToNull(parsed.ServiceId),
            endpoint_id = EmptyToNull(parsed.EndpointId),
            wait = "complete",
        }));
        await _chatRunActorPort.ObserveSubRunTerminalAsync(actorId, terminal, ct);

        return (await readySource.Task.WaitAsync(ct)).ResultJson;
    }

    private static ParsedDispatchResult NormalizeDispatchResultForObservation(
        ToolCall toolCall,
        string argumentsJson,
        ParsedDispatchResult parsed)
    {
        if (string.Equals(toolCall.Name, "aevatar_invoke_gagent", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(parsed.ActorId) &&
            TryReadString(argumentsJson, "actor_id", out var actorId))
        {
            return parsed with { ActorId = actorId };
        }

        return parsed;
    }

    private async Task<ChatRunSubRunTerminalObserved?> ResolveTerminalAsync(
        string toolName,
        ParsedDispatchResult dispatch,
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
        ParsedDispatchResult dispatch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ActorId) && !string.IsNullOrWhiteSpace(dispatch.RunId))
        {
            var snapshot = await _gagentTerminalQueryPort.GetByCorrelationIdAsync(
                dispatch.ActorId,
                dispatch.RunId,
                ct);
            if (snapshot != null)
                return BuildTerminal(dispatch, snapshot.Status.ToString(), ToJson(snapshot));
        }

        return null;
    }

    private async Task<ChatRunSubRunTerminalObserved?> ResolveTeamTerminalAsync(
        ParsedDispatchResult dispatch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ServiceId) && !string.IsNullOrWhiteSpace(dispatch.RunId))
        {
            var snapshot = await _serviceRunQueryPort.GetByRunIdAsync(
                dispatch.ScopeId,
                dispatch.ServiceId,
                dispatch.RunId,
                ct);
            if (snapshot != null)
                return BuildTerminal(dispatch, snapshot.Status.ToString(), ToJson(snapshot));
        }

        return null;
    }

    private async Task<ChatRunSubRunTerminalObserved?> ResolveWorkflowTerminalAsync(
        ParsedDispatchResult dispatch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(dispatch.ActorId))
        {
            var snapshot = await _workflowQueryService.GetActorSnapshotAsync(dispatch.ActorId, ct);
            if (snapshot != null)
                return BuildTerminal(dispatch, snapshot.CompletionStatus.ToString(), ProtoJson(snapshot));
        }

        return null;
    }

    private static ChatRunSubRunTerminalObserved BuildTerminal(
        ParsedDispatchResult dispatch,
        string status,
        string resultJson) =>
        new()
        {
            RunId = dispatch.RunId,
            Status = status,
            ResultJson = resultJson,
            ActorId = dispatch.ActorId,
            ServiceId = dispatch.ServiceId,
            EndpointId = dispatch.EndpointId,
            ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private static ParsedDispatchResult ParseDispatchResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ParsedDispatchResult.Empty;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            return ParsedDispatchResult.Empty with
            {
                ErrorCode = ReadString(error, "code"),
            };
        }

        return new ParsedDispatchResult(
            RunId: ReadString(root, "run_id"),
            ActorId: ReadString(root, "actor_id"),
            ServiceId: ReadString(root, "service_id"),
            EndpointId: ReadString(root, "endpoint_id"),
            StreamTopic: ReadString(root, "stream_topic"),
            ScopeId: ReadScopeId(root),
            ErrorCode: string.Empty);
    }

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

    private static string ReadScopeId(JsonElement root)
    {
        if (root.TryGetProperty("scope_id", out var scopeId) &&
            scopeId.ValueKind == JsonValueKind.String)
        {
            return scopeId.GetString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

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

    private static string ToJson<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static string ProtoJson(IMessage message) =>
        JsonFormatter.Default.Format(message);

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record ParsedDispatchResult(
        string RunId,
        string ActorId,
        string ServiceId,
        string EndpointId,
        string StreamTopic,
        string ScopeId,
        string ErrorCode)
    {
        public static ParsedDispatchResult Empty { get; } = new(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }

}
