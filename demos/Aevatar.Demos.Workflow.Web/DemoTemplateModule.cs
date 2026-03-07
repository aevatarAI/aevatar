using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoTemplateModule : IEventModule, IWorkflowPrimitiveHandler
{
    public string Name => "demo_template";
    public int Priority => -10;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(ChatRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (!IsSupportedStepType(request.StepType))
                return;

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

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = true,
                Output = output,
            }, EventDirection.Self, ct);
            return;
        }

        if (!payload.Is(ChatRequestEvent.Descriptor))
            return;

        // Only intercept role-level ChatRequest events. Root workflow actor ChatRequest
        // must continue into WorkflowGAgent -> StartWorkflow flow.
        if (ctx.AgentId.IndexOf(':', StringComparison.Ordinal) < 0)
            return;

        var chatRequest = payload.Unpack<ChatRequestEvent>();
        var inputPrompt = chatRequest.Prompt ?? string.Empty;
        var outputText = $"[demo_template role module]\nIncident '{inputPrompt}' has been normalized by role event_modules.";
        var response = new ChatResponseEvent
        {
            SessionId = chatRequest.SessionId ?? string.Empty,
            Content = outputText,
        };

        await ctx.PublishAsync(response, EventDirection.Up, ct);

        // Replace payload to prevent the default RoleGAgent ChatRequest handler from invoking LLM.
        envelope.Payload = Any.Pack(response);
    }

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
