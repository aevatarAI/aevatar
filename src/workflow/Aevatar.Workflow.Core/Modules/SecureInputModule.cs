using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Secure human input module. Suspends the workflow, captures a secret value,
/// and only emits a redacted completion output while keeping the raw value in
/// actor-local runtime store without placing it on event payload fields.
/// </summary>
public sealed class SecureInputModule : IEventModule<IWorkflowExecutionContext>
{
    private readonly Dictionary<(string RunId, string StepId), StepRequestEvent> _pending = [];

    private const string DefaultMaskedOutput = "[secure input captured]";

    public string Name => "secure_input";
    public int Priority => 5;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(WorkflowResumedEvent.Descriptor) ||
                payload.Is(WorkflowCompletedEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            var workflowCompleted = payload.Unpack<WorkflowCompletedEvent>();
            SecureValueRuntimeStore.RemoveRun(ctx.AgentId, workflowCompleted.RunId);
            return;
        }

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (!string.Equals(request.StepType, Name, StringComparison.OrdinalIgnoreCase))
                return;

            var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
            var prompt = WorkflowParameterValueParser.GetString(
                request.Parameters,
                "Please provide the secure value:",
                "prompt",
                "message");
            var variable = WorkflowParameterValueParser.GetString(
                request.Parameters,
                "secure_input",
                "variable");
            var timeoutSeconds = WorkflowParameterValueParser.ResolveTimeoutSeconds(
                request.Parameters,
                defaultSeconds: 1800);

            _pending[(runId, request.StepId)] = request;

            ctx.Logger.LogInformation(
                "SecureInput: run={RunId} step={StepId} suspended, variable={Variable}, timeout={Timeout}s",
                runId,
                request.StepId,
                variable,
                timeoutSeconds);

            var suspended = new WorkflowSuspendedEvent
            {
                RunId = runId,
                StepId = request.StepId,
                SuspensionType = Name,
                Prompt = prompt,
                TimeoutSeconds = timeoutSeconds,
                VariableName = variable,
            };
            suspended.Metadata["variable"] = variable;
            suspended.Metadata["secure"] = "true";
            suspended.Metadata["input_mode"] = "password";
            suspended.Metadata["redacted_output"] = ResolveMaskedOutput(request.Parameters);

            await ctx.PublishAsync(suspended, EventDirection.Both, ct);
            return;
        }

        var resumed = payload.Unpack<WorkflowResumedEvent>();
        if (!TryResolvePending(resumed, out var pendingKey, out var pending))
            return;

        _pending.Remove(pendingKey);

        var userInput = resumed.UserInput ?? string.Empty;
        var onTimeout = pending.Parameters.GetValueOrDefault("on_timeout", "fail");
        var allowEmpty = bool.TryParse(
            WorkflowParameterValueParser.GetString(
                pending.Parameters,
                "false",
                "allow_empty",
                "allowEmpty"),
            out var parsedAllowEmpty) && parsedAllowEmpty;
        var variableName = WorkflowParameterValueParser.GetString(
            pending.Parameters,
            "secure_input",
            "variable");
        var maskedOutput = ResolveMaskedOutput(pending.Parameters);

        if (string.IsNullOrEmpty(userInput) && !resumed.Approved)
        {
            ctx.Logger.LogWarning(
                "SecureInput: run={RunId} step={StepId} timed out or cancelled",
                pending.RunId,
                pending.StepId);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                Success = onTimeout != "fail",
                Output = pending.Input,
                Error = onTimeout == "fail" ? "Secure input timed out" : "",
            }, EventDirection.Self, ct);
            return;
        }

        if (string.IsNullOrEmpty(userInput) && !allowEmpty)
        {
            ctx.Logger.LogWarning(
                "SecureInput: run={RunId} step={StepId} rejected empty secure value",
                pending.RunId,
                pending.StepId);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = pending.RunId,
                Success = false,
                Error = "Secure input is required",
            }, EventDirection.Self, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "SecureInput: run={RunId} step={StepId} captured secure value ({Len} chars)",
            pending.RunId,
            pending.StepId,
            userInput.Length);

        SecureValueRuntimeStore.Set(ctx.AgentId, pending.RunId, variableName, userInput);

        await ctx.PublishAsync(new SecureValueCapturedEvent
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            Variable = variableName,
            // Keep payload redacted. Raw value remains in actor-local runtime store.
            Value = string.Empty,
        }, EventDirection.Self, ct);

        var stepCompleted = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            Success = true,
            Output = maskedOutput,
        };
        stepCompleted.Annotations["secure.input"] = "true";
        stepCompleted.Annotations["secure.variable"] = variableName;
        stepCompleted.Annotations["secure.redacted_output"] = maskedOutput;
        await ctx.PublishAsync(stepCompleted, EventDirection.Self, ct);
    }

    private static string ResolveMaskedOutput(IReadOnlyDictionary<string, string> parameters) =>
        WorkflowParameterValueParser.GetString(
            parameters,
            DefaultMaskedOutput,
            "redacted_output",
            "masked_output",
            "display_value");

    private bool TryResolvePending(
        WorkflowResumedEvent resumed,
        out (string RunId, string StepId) pendingKey,
        out StepRequestEvent pending)
    {
        if (!string.IsNullOrWhiteSpace(resumed.RunId))
        {
            pendingKey = (WorkflowRunIdNormalizer.Normalize(resumed.RunId), resumed.StepId);
            return _pending.TryGetValue(pendingKey, out pending!);
        }

        var matchCount = 0;
        pendingKey = default;
        pending = default!;
        foreach (var entry in _pending)
        {
            if (!entry.Key.StepId.Equals(resumed.StepId, StringComparison.Ordinal))
                continue;

            matchCount++;
            if (matchCount > 1)
                return false;

            pendingKey = entry.Key;
            pending = entry.Value;
        }

        return matchCount == 1;
    }
}
