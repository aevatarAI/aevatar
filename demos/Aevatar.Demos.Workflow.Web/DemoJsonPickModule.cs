using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoJsonPickModule
    : IEventModule<IWorkflowExecutionContext>,
        IEventModule<IEventHandlerContext>
{
    public string Name => "demo_json_pick";
    public int Priority => -10;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(ChatRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null || !payload.Is(StepRequestEvent.Descriptor))
            return;

        var request = payload.Unpack<StepRequestEvent>();
        if (!IsSupportedStepType(request.StepType))
            return;

        var input = request.Input ?? string.Empty;
        var path = request.Parameters.GetValueOrDefault("path", "$");

        try
        {
            using var document = JsonDocument.Parse(input);
            if (!TryResolvePath(document.RootElement, path, out var resolved))
            {
                await PublishFailureAsync(request, $"Path '{path}' not found.", ctx, ct);
                return;
            }

            var output = resolved.ValueKind == JsonValueKind.String
                ? resolved.GetString() ?? string.Empty
                : resolved.GetRawText();

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = true,
                Output = output,
            }, EventDirection.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishFailureAsync(request, $"Invalid JSON input: {ex.Message}", ctx, ct);
        }
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null || !payload.Is(ChatRequestEvent.Descriptor))
            return;

        // Only intercept role-level ChatRequest events. Root workflow actor ChatRequest
        // must continue into WorkflowGAgent -> StartWorkflow flow.
        if (ctx.AgentId.IndexOf(':', StringComparison.Ordinal) < 0)
            return;

        var chatRequest = payload.Unpack<ChatRequestEvent>();
        var outputText = ResolveRoleOutput(chatRequest.Prompt ?? string.Empty, "incident.owner.team");
        var response = new ChatResponseEvent
        {
            SessionId = chatRequest.SessionId ?? string.Empty,
            Content = outputText,
        };

        await ctx.PublishAsync(response, EventDirection.Up, ct);

        // Replace payload to prevent the default RoleGAgent ChatRequest handler from invoking LLM.
        envelope.Payload = Any.Pack(response);
    }

    private static bool IsSupportedStepType(string stepType) =>
        string.Equals(stepType, "demo_json_pick", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stepType, "demo_json_path", StringComparison.OrdinalIgnoreCase);

    private static string ResolveRoleOutput(string rawInput, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(rawInput);
            if (!TryResolvePath(document.RootElement, path, out var resolved))
                return $"[demo_json_pick role module] Path '{path}' not found.";

            var value = resolved.ValueKind == JsonValueKind.String
                ? resolved.GetString() ?? string.Empty
                : resolved.GetRawText();
            return $"[demo_json_pick role module] {path} = {value}";
        }
        catch (Exception ex)
        {
            return $"[demo_json_pick role module] Invalid JSON input: {ex.Message}";
        }
    }

    private static bool TryResolvePath(JsonElement root, string path, out JsonElement resolved)
    {
        resolved = root;
        if (string.IsNullOrWhiteSpace(path) || path == "$")
            return true;

        var segments = path.Trim().TrimStart('$').TrimStart('.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (resolved.ValueKind != JsonValueKind.Object || !resolved.TryGetProperty(segment, out var next))
                return false;

            resolved = next;
        }

        return true;
    }

    private static Task PublishFailureAsync(
        StepRequestEvent request,
        string error,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        return ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);
    }
}
