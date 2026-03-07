using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoTemplateModule : IWorkflowPrimitiveHandler
{
    public string Name => "demo_template";

    public Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct) =>
        HandleStepAsync(request, ctx.PublishAsync, ct);

    private static Task HandleStepAsync(
        StepRequestEvent request,
        Func<StepCompletedEvent, EventDirection, CancellationToken, Task> publishAsync,
        CancellationToken ct)
    {
        if (!IsSupportedStepType(request.StepType))
            return Task.CompletedTask;

        var input = request.Input ?? string.Empty;
        var template = request.Parameters.GetValueOrDefault("template", "{{input}}");
        var rendered = template.Replace("{{input}}", input, StringComparison.Ordinal);

        foreach (var (key, value) in request.Parameters)
        {
            var token = $"{{{{param.{key}}}}}";
            rendered = rendered.Replace(token, value ?? string.Empty, StringComparison.Ordinal);
        }

        var prefix = request.Parameters.GetValueOrDefault("prefix", string.Empty);
        var suffix = request.Parameters.GetValueOrDefault("suffix", string.Empty);
        var upper = string.Equals(request.Parameters.GetValueOrDefault("uppercase", "false"), "true",
            StringComparison.OrdinalIgnoreCase);

        var output = $"{prefix}{rendered}{suffix}";
        if (upper)
            output = output.ToUpperInvariant();

        return publishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = output,
        }, EventDirection.Self, ct);
    }

    private static bool IsSupportedStepType(string stepType) =>
        string.Equals(stepType, "demo_template", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stepType, "demo_format", StringComparison.OrdinalIgnoreCase);
}
