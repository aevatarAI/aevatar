using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Secure human input module. Suspends the workflow, captures a secret value,
/// and only emits a redacted completion output while keeping the raw value in
/// typed actor state instead of observable event payload.
/// </summary>
public sealed class SecureInputModule : IEventModule<IWorkflowExecutionContext>
{
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
            var state = SecureInputStateAccess.Load(ctx);
            SecureInputStateAccess.RemoveRun(state, workflowCompleted.RunId);
            await SecureInputStateAccess.SaveAsync(state, ctx, ct);
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
            var requestVariableName = NormalizeVariableName(WorkflowParameterValueParser.GetString(
                request.Parameters,
                "secure_input",
                "variable"));
            var timeoutSeconds = WorkflowParameterValueParser.ResolveTimeoutSeconds(
                request.Parameters,
                defaultSeconds: 1800);
            var requestAllowEmpty = bool.TryParse(
                WorkflowParameterValueParser.GetString(
                    request.Parameters,
                    "false",
                    "allow_empty",
                    "allowEmpty"),
                out var parsedAllowEmpty) && parsedAllowEmpty;
            var requestMaskedOutput = ResolveMaskedOutput(request.Parameters);

            var state = SecureInputStateAccess.Load(ctx);
            state.Pending[SecureInputStateAccess.BuildPendingKey(runId, request.StepId)] = new PendingSecureInputState
            {
                StepId = request.StepId,
                RunId = runId,
                Input = request.Input ?? string.Empty,
                OnTimeout = request.Parameters.GetValueOrDefault("on_timeout", "fail"),
                AllowEmpty = requestAllowEmpty,
                VariableName = requestVariableName,
                MaskedOutput = requestMaskedOutput,
            };
            await SecureInputStateAccess.SaveAsync(state, ctx, ct);
            await SecureInputRuntimeContextAccess.RemoveCapturedValueAsync(ctx, runId, requestVariableName, ct);

            ctx.Logger.LogInformation(
                "SecureInput: run={RunId} step={StepId} suspended, variable={Variable}, timeout={Timeout}s",
                runId,
                request.StepId,
                requestVariableName,
                timeoutSeconds);

            // Refactor (iter163/cluster-003-workflow-suspension-legacy-metadata):
            //   Old pattern: reserved secure input values could be recovered from WorkflowSuspendedEvent.Metadata.
            //   New principle: publish typed suspension fields; Metadata remains open extension data only.
            var suspended = new WorkflowSuspendedEvent
            {
                RunId = runId,
                StepId = request.StepId,
                SuspensionType = Name,
                Prompt = prompt,
                TimeoutSeconds = timeoutSeconds,
                VariableName = requestVariableName,
                Secure = true,
                RedactedOutput = requestMaskedOutput,
            };
            WorkflowSuspensionRequestSupport.ApplyDeliveryTarget(suspended, request);

            await ctx.PublishAsync(suspended, TopologyAudience.ParentAndChildren, ct);
            return;
        }

        var resumed = payload.Unpack<WorkflowResumedEvent>();
        var stateForResume = SecureInputStateAccess.Load(ctx);
        if (!TryResolvePending(stateForResume, resumed, out var pendingKey, out var pending))
            return;

        var userInput = resumed.UserInput ?? string.Empty;
        var onTimeout = pending.OnTimeout;
        var allowEmpty = pending.AllowEmpty;
        var variableName = NormalizeVariableName(pending.VariableName);
        var maskedOutput = string.IsNullOrWhiteSpace(pending.MaskedOutput) ? DefaultMaskedOutput : pending.MaskedOutput;

        if (string.IsNullOrEmpty(userInput) && !resumed.Approved)
        {
            stateForResume.Pending.Remove(pendingKey);
            await SecureInputStateAccess.SaveAsync(stateForResume, ctx, ct);
            await SecureInputRuntimeContextAccess.RemoveCapturedValueAsync(ctx, pending.RunId, variableName, ct);

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
            }, TopologyAudience.Self, ct);
            return;
        }

        if (string.IsNullOrEmpty(userInput) && !allowEmpty)
        {
            stateForResume.Pending.Remove(pendingKey);
            await SecureInputStateAccess.SaveAsync(stateForResume, ctx, ct);
            await SecureInputRuntimeContextAccess.RemoveCapturedValueAsync(ctx, pending.RunId, variableName, ct);

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
            }, TopologyAudience.Self, ct);
            return;
        }

        ctx.Logger.LogInformation(
            "SecureInput: run={RunId} step={StepId} captured secure value ({Len} chars)",
            pending.RunId,
            pending.StepId,
            userInput.Length);

        var valueReference = await SecureInputRuntimeContextAccess.StoreCapturedValueReferenceAsync(
            ctx,
            pending.RunId,
            variableName,
            userInput,
            ct);
        stateForResume.Pending.Remove(pendingKey);
        SecureInputStateAccess.SetCapturedReference(stateForResume, pending.RunId, variableName, valueReference);
        await SecureInputStateAccess.SaveAsync(stateForResume, ctx, ct);

        await ctx.PublishAsync(new SecureValueCapturedEvent
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            Variable = variableName,
            // Keep payload redacted. The typed reference is persisted in actor-owned module state.
            Value = string.Empty,
            ValueReference = valueReference.Clone(),
        }, TopologyAudience.Self, ct);

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
        await ctx.PublishAsync(stepCompleted, TopologyAudience.Self, ct);
    }

    private static string ResolveMaskedOutput(IReadOnlyDictionary<string, string> parameters) =>
        WorkflowParameterValueParser.GetString(
            parameters,
            DefaultMaskedOutput,
            "redacted_output",
            "masked_output",
            "display_value");

    private static string NormalizeVariableName(string? variable) =>
        string.IsNullOrWhiteSpace(variable) ? "secure_input" : variable.Trim();

    private bool TryResolvePending(
        SecureInputModuleState state,
        WorkflowResumedEvent resumed,
        out string pendingKey,
        out PendingSecureInputState pending)
    {
        if (!string.IsNullOrWhiteSpace(resumed.RunId))
        {
            pendingKey = SecureInputStateAccess.BuildPendingKey(resumed.RunId, resumed.StepId);
            return state.Pending.TryGetValue(pendingKey, out pending!);
        }

        var matchCount = 0;
        pendingKey = string.Empty;
        pending = new PendingSecureInputState();
        foreach (var entry in state.Pending)
        {
            if (!entry.Value.StepId.Equals(resumed.StepId, StringComparison.Ordinal))
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
