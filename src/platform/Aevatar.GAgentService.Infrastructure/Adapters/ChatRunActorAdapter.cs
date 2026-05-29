using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class ChatRunActorAdapter : IChatRunActorPort
{
    private const string PublisherId = "gagent-service.chat-runs";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public ChatRunActorAdapter(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<string> StartAsync(
        ChatRunStartRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var responseId = NormalizeRequired(request.ResponseId, nameof(request.ResponseId));
        var actorId = $"chat-run:{responseId}";
        var actor = await _runtime.CreateAsync<ChatRunActor>(actorId, ct);
        var command = new StartChatRunRequested
        {
            ResponseId = responseId,
            ModelName = request.ModelName?.Trim() ?? string.Empty,
            StartedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };
        if (request.IdleTtl.HasValue)
            command.IdleTtl = Duration.FromTimeSpan(request.IdleTtl.Value);
        command.Messages.Add(request.Messages.Select(ToRecord));

        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(actor.Id, Any.Pack(command), $"{responseId}:chat-run:start"),
            ct);
        return actor.Id;
    }

    public async Task SubmitToolCallAsync(
        string chatRunActorId,
        ChatRunToolCompletionRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatRunActorId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ToolCall);

        var target = ResolveObservationTarget(request);
        var command = new SubmitChatRunToolCallRequested
        {
            ToolCallId = NormalizeRequired(request.ToolCall.Id, nameof(request.ToolCall.Id)),
            ToolName = NormalizeRequired(request.ToolCall.Name, nameof(request.ToolCall.Name)),
            Arguments = ParseStruct(request.ArgumentsJson),
            ResultJson = request.ToolExecutionResultJson ?? string.Empty,
            RunId = target.RunId,
            TargetKind = ResolveTargetKind(request.ToolCall.Name),
            TargetId = ResolveTargetId(target),
            WaitMode = target.WaitMode,
            StreamTopic = target.StreamTopic,
            ActorId = target.ActorId,
            ServiceId = target.ServiceId,
            EndpointId = target.EndpointId,
            LlmRound = request.LlmRound,
            ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        await _dispatchPort.DispatchAsync(
            chatRunActorId,
            CreateEnvelope(chatRunActorId, Any.Pack(command), $"{request.ResponseId}:tool:{request.ToolCall.Id}:submitted"),
            ct);
    }

    public async Task BeginSubRunObservationAsync(
        string chatRunActorId,
        ChatRunToolCompletionRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatRunActorId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ToolCall);

        var target = ResolveObservationTarget(request);
        if (target.WaitMode != ChatRunSubRunWaitMode.Complete || string.IsNullOrWhiteSpace(target.RunId))
            return;

        var command = new PrepareChatRunSubRunObservationRequested
        {
            ToolCallId = NormalizeRequired(request.ToolCall.Id, nameof(request.ToolCall.Id)),
            ToolName = NormalizeRequired(request.ToolCall.Name, nameof(request.ToolCall.Name)),
            Arguments = ParseStruct(request.ArgumentsJson),
            RunId = target.RunId,
            TargetKind = ResolveTargetKind(request.ToolCall.Name),
            TargetId = ResolveTargetId(target),
            WaitMode = target.WaitMode,
            StreamTopic = target.StreamTopic,
            ActorId = target.ActorId,
            ServiceId = target.ServiceId,
            EndpointId = target.EndpointId,
            LlmRound = request.LlmRound,
            ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        await _dispatchPort.DispatchAsync(
            chatRunActorId,
            CreateEnvelope(
                chatRunActorId,
                Any.Pack(command),
                $"{request.ResponseId}:tool:{request.ToolCall.Id}:observe"),
            ct);
    }

    // Refactor (iter290/cluster001): Old pattern: actor observation target was inferred from ResultJson. New principle: actor observation uses typed run, target, and wait fields before dispatch.
    private static ChatRunToolCompletionRequest ResolveObservationTarget(ChatRunToolCompletionRequest request)
    {
        if (request.WaitMode == ChatRunSubRunWaitMode.Complete && !string.IsNullOrWhiteSpace(request.RunId))
            return request;

        if (string.Equals(request.ToolCall.Name, "aevatar_invoke_gagent", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(request.ToolCall.Id) &&
            TryReadString(request.ArgumentsJson, "actor_id", out var actorId))
        {
            return request with
            {
                RunId = request.ToolCall.Id.Trim(),
                ActorId = actorId,
                WaitMode = ChatRunSubRunWaitMode.Complete,
            };
        }

        return request;
    }

    public Task ObserveSubRunTerminalAsync(
        string chatRunActorId,
        ChatRunSubRunTerminalObserved observed,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatRunActorId);
        ArgumentNullException.ThrowIfNull(observed);

        return _dispatchPort.DispatchAsync(
            chatRunActorId,
            CreateEnvelope(chatRunActorId, Any.Pack(observed), $"{observed.RunId}:terminal"),
            ct);
    }

    public Task TerminateAsync(
        string chatRunActorId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatRunActorId);

        return _dispatchPort.DispatchAsync(
            chatRunActorId,
            CreateEnvelope(chatRunActorId, Any.Pack(new TerminateChatRunRequested
            {
                Reason = reason ?? string.Empty,
                ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }), $"{chatRunActorId}:terminate:{Guid.NewGuid():N}"),
            ct);
    }

    private static ChatRunMessageRecord ToRecord(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var record = new ChatRunMessageRecord
        {
            Role = message.Role ?? string.Empty,
            Content = message.Content ?? string.Empty,
            ToolCallId = message.ToolCallId ?? string.Empty,
        };
        if (message.ContentParts is { Count: > 0 })
            record.ContentParts.Add(message.ContentParts.Select(ToRecord));
        if (message.ToolCalls is { Count: > 0 })
            record.AssistantToolCalls.Add(message.ToolCalls.Select(ToRecord));

        return record;
    }

    private static ChatRunContentPart ToRecord(ContentPart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        return new ChatRunContentPart
        {
            Kind = part.Kind.ToString(),
            Text = part.Text ?? string.Empty,
            DataBase64 = part.DataBase64 ?? string.Empty,
            MediaType = part.MediaType ?? string.Empty,
            Uri = part.Uri ?? string.Empty,
            Name = part.Name ?? string.Empty,
        };
    }

    private static ChatRunToolCallRecord ToRecord(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        return new ChatRunToolCallRecord
        {
            ToolCallId = call.Id ?? string.Empty,
            ToolName = call.Name ?? string.Empty,
            Arguments = ParseStruct(call.ArgumentsJson),
        };
    }

    private static Struct ParseStruct(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Struct();

        try
        {
            return JsonParser.Default.Parse<Struct>(json);
        }
        catch
        {
            return new Struct();
        }
    }

    private static string ResolveTargetId(ChatRunToolCompletionRequest result) =>
        FirstNonEmpty(result.ActorId, result.ServiceId, result.EndpointId);

    private static ChatRunSubRunTargetKind ResolveTargetKind(string toolName) =>
        toolName switch
        {
            "aevatar_invoke_gagent" => ChatRunSubRunTargetKind.Gagent,
            "aevatar_invoke_team" => ChatRunSubRunTargetKind.Team,
            "aevatar_start_workflow" => ChatRunSubRunTargetKind.Workflow,
            _ => ChatRunSubRunTargetKind.Unspecified,
        };

    private static bool TryReadString(
        string? json,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var arguments = JsonParser.Default.Parse<Struct>(json);
            if (arguments.Fields.TryGetValue(propertyName, out var property) &&
                property.KindCase == Value.KindOneofCase.StringValue)
            {
                value = property.StringValue?.Trim() ?? string.Empty;
            }

            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static EventEnvelope CreateEnvelope(
        string actorId,
        Any payload,
        string commandId) =>
        new()
        {
            Id = string.IsNullOrWhiteSpace(commandId) ? Guid.NewGuid().ToString("N") : commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload,
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
        };

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
