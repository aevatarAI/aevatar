using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunHumanInteractionRuntime
    : IWorkflowStepFamilyHandler
{
    private static readonly string[] SupportedTypes = ["human_input", "human_approval"];

    private readonly WorkflowRunRuntimeContext _context;

    public WorkflowRunHumanInteractionRuntime(WorkflowRunRuntimeContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public IReadOnlyCollection<string> SupportedStepTypes => SupportedTypes;

    public Task HandleStepRequestAsync(StepRequestEvent request, CancellationToken ct) =>
        WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType) switch
        {
            "human_input" => HandleHumanGateStepRequestAsync(request, "human_input", ct),
            "human_approval" => HandleHumanGateStepRequestAsync(request, "human_approval", ct),
            _ => Task.CompletedTask,
        };

    public async Task HandleHumanGateStepRequestAsync(StepRequestEvent request, string gateType, CancellationToken ct)
    {
        var timeoutSeconds = WorkflowParameterValueParser.ResolveTimeoutSeconds(
            request.Parameters,
            gateType == "human_input" ? 1800 : 3600);
        var prompt = WorkflowParameterValueParser.GetString(
            request.Parameters,
            gateType == "human_input" ? "Please provide input:" : "Approve this step?",
            "prompt",
            "message");
        var variable = WorkflowParameterValueParser.GetString(request.Parameters, "user_input", "variable");
        var onTimeout = WorkflowParameterValueParser.GetString(request.Parameters, "fail", "on_timeout");
        var onReject = WorkflowParameterValueParser.GetString(request.Parameters, "fail", "on_reject");

        var state = _context.State;
        var next = state.Clone();
        next.Status = "suspended";
        next.PendingHumanGates[request.StepId] = new WorkflowPendingHumanGateState
        {
            StepId = request.StepId,
            GateType = gateType,
            Input = request.Input ?? string.Empty,
            Prompt = prompt,
            Variable = variable,
            TimeoutSeconds = timeoutSeconds,
            OnTimeout = onTimeout,
            OnReject = onReject,
            ResumeToken = Guid.NewGuid().ToString("N"),
        };
        await _context.PersistStateAsync(next, ct);

        var suspended = new WorkflowSuspendedEvent
        {
            RunId = state.RunId,
            StepId = request.StepId,
            SuspensionType = gateType,
            Prompt = prompt,
            TimeoutSeconds = timeoutSeconds,
            ResumeToken = next.PendingHumanGates[request.StepId].ResumeToken,
        };
        if (!string.IsNullOrWhiteSpace(variable))
            suspended.Metadata["variable"] = variable;
        if (!string.IsNullOrWhiteSpace(onTimeout))
            suspended.Metadata["on_timeout"] = onTimeout;
        if (!string.IsNullOrWhiteSpace(onReject))
            suspended.Metadata["on_reject"] = onReject;
        suspended.Metadata["resume_token"] = next.PendingHumanGates[request.StepId].ResumeToken;
        await _context.PublishAsync(suspended, EventDirection.Both, ct);
    }
}
