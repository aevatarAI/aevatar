// ─────────────────────────────────────────────────────────────
// WhileModule — 循环模块
// 重复执行子步骤序列直到条件不满足或达到最大迭代次数
//
// If the step definition has Children, each iteration dispatches
// them sequentially (e.g. verify → build_dag → next_round).
// Otherwise falls back to dispatching a single step type.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>循环模块。处理 type=while 的步骤。</summary>
public sealed class WhileModule : IEventModule
{
    private WorkflowDefinition? _workflow;
    private readonly Dictionary<string, WhileState> _activeLoops = [];
    private readonly Dictionary<string, string> _pendingChildren = []; // childStepId → whileStepId

    public string Name => "while";
    public int Priority => 5;

    /// <summary>设置工作流定义，用于查找步骤的 Children。</summary>
    public void SetWorkflow(WorkflowDefinition workflow) => _workflow = workflow;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(StepCompletedEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "while") return;

            var maxIterations = int.TryParse(
                request.Parameters.GetValueOrDefault("max_iterations", "10"), out var max) ? max : 10;

            // Resolve children from workflow definition
            var children = ResolveChildren(request.StepId);
            if (children == null || children.Count == 0)
            {
                // Fallback: no children defined — dispatch a single step type per iteration (legacy behavior)
                var subStepType = request.Parameters.GetValueOrDefault("step", "llm_call");
                children =
                [
                    new StepDefinition
                    {
                        Id = "",
                        Type = subStepType,
                        TargetRole = request.TargetRole,
                    },
                ];
            }

            var state = new WhileState
            {
                MaxIterations = maxIterations,
                CurrentIteration = 0,
                CurrentChildIndex = 0,
                Children = children,
                Input = request.Input,
                FallbackRole = request.TargetRole,
            };
            _activeLoops[request.StepId] = state;

            ctx.Logger.LogInformation(
                "While {StepId}: start, max={Max}, children={Count}",
                request.StepId, maxIterations, children.Count);

            await DispatchCurrentChild(request.StepId, state, ctx, ct);
        }
        else if (payload.Is(StepCompletedEvent.Descriptor))
        {
            var completed = payload.Unpack<StepCompletedEvent>();

            // Match this completion to an active while loop's pending child
            if (!_pendingChildren.TryGetValue(completed.StepId, out var whileStepId)) return;
            _pendingChildren.Remove(completed.StepId);

            if (!_activeLoops.TryGetValue(whileStepId, out var state)) return;

            // Carry forward output to next child
            state.Input = completed.Output;
            state.CurrentChildIndex++;

            if (state.CurrentChildIndex < state.Children.Count)
            {
                // More children in this iteration — dispatch next
                await DispatchCurrentChild(whileStepId, state, ctx, ct);
            }
            else
            {
                // All children in this iteration completed
                state.CurrentIteration++;

                var shouldContinue = completed.Success &&
                                     state.CurrentIteration < state.MaxIterations;

                if (shouldContinue)
                {
                    state.CurrentChildIndex = 0;
                    ctx.Logger.LogInformation(
                        "While {StepId}: iteration {Iter} starting",
                        whileStepId, state.CurrentIteration);
                    await DispatchCurrentChild(whileStepId, state, ctx, ct);
                }
                else
                {
                    ctx.Logger.LogInformation(
                        "While {StepId}: completed after {Iter} iterations",
                        whileStepId, state.CurrentIteration);
                    _activeLoops.Remove(whileStepId);
                    await ctx.PublishAsync(new StepCompletedEvent
                    {
                        StepId = whileStepId,
                        Success = true,
                        Output = completed.Output,
                    }, EventDirection.Self, ct);
                }
            }
        }
    }

    private List<StepDefinition>? ResolveChildren(string stepId)
    {
        if (_workflow == null) return null;
        var step = _workflow.GetStep(stepId);
        return step?.Children is { Count: > 0 } ? step.Children : null;
    }

    private async Task DispatchCurrentChild(
        string whileStepId, WhileState state, IEventHandlerContext ctx, CancellationToken ct)
    {
        var child = state.Children[state.CurrentChildIndex];
        var childStepId = string.IsNullOrEmpty(child.Id)
            ? $"{whileStepId}_iter_{state.CurrentIteration}"
            : $"{whileStepId}_iter_{state.CurrentIteration}_{child.Id}";

        _pendingChildren[childStepId] = whileStepId;

        var req = new StepRequestEvent
        {
            StepId = childStepId,
            StepType = child.Type,
            Input = state.Input,
            TargetRole = child.TargetRole ?? state.FallbackRole,
        };
        foreach (var (k, v) in child.Parameters)
            req.Parameters[k] = v;

        // Inject allowed_connectors from role definition
        if (!string.IsNullOrWhiteSpace(req.TargetRole) && _workflow != null)
        {
            var role = _workflow.Roles.FirstOrDefault(
                r => string.Equals(r.Id, req.TargetRole, StringComparison.OrdinalIgnoreCase));
            if (role is { Connectors.Count: > 0 })
                req.Parameters["allowed_connectors"] = string.Join(",", role.Connectors);
        }

        ctx.Logger.LogInformation(
            "While {WhileStep}: dispatch child={ChildId} type={Type} role={Role} iter={Iter}/{Max}",
            whileStepId, childStepId, child.Type, req.TargetRole,
            state.CurrentIteration, state.MaxIterations);

        await ctx.PublishAsync(req, EventDirection.Self, ct);
    }

    private sealed class WhileState
    {
        public required int MaxIterations { get; init; }
        public int CurrentIteration { get; set; }
        public int CurrentChildIndex { get; set; }
        public required List<StepDefinition> Children { get; init; }
        public required string Input { get; set; }
        public required string FallbackRole { get; init; }
    }
}
