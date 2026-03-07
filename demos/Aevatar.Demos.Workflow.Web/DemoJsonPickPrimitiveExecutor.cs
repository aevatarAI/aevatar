using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoJsonPickPrimitiveExecutor : IWorkflowPrimitiveExecutor
{
    public string Name => "demo_json_pick";

    public async Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
        if (!IsSupportedStepType(request.StepType))
            return;

        var input = request.Input ?? string.Empty;
        var path = request.Parameters.GetValueOrDefault("path", "$");

        try
        {
            using var document = JsonDocument.Parse(input);
            if (!TryResolvePath(document.RootElement, path, out var resolved))
            {
                await PublishFailureAsync(request, $"Path '{path}' not found.", ctx.PublishAsync, ct);
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
            await PublishFailureAsync(request, $"Invalid JSON input: {ex.Message}", ctx.PublishAsync, ct);
        }
    }

    private static bool IsSupportedStepType(string stepType) =>
        string.Equals(stepType, "demo_json_pick", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stepType, "demo_json_path", StringComparison.OrdinalIgnoreCase);

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
        Func<StepCompletedEvent, EventDirection, CancellationToken, Task> publishAsync,
        CancellationToken ct)
    {
        return publishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);
    }
}
