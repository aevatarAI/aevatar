using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Validation;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Primitives;

/// <summary>
/// Encapsulates workflow_call runtime orchestration for <see cref="WorkflowRunGAgent"/>.
/// Keeps sub-workflow actor lifecycle and state transition helpers out of the run actor.
/// </summary>
internal sealed class SubWorkflowOrchestrator
{
    private static readonly WorkflowParser DefinitionParser = new();
    private const int DefaultDefinitionResolutionTimeoutMs = 30_000;
    private const string WorkflowCallMetadataPrefix = "workflow_call.";
    private const string WorkflowCallInvocationIdMetadataKey = WorkflowCallMetadataPrefix + "invocation_id";
    private const string WorkflowCallWorkflowNameMetadataKey = WorkflowCallMetadataPrefix + "workflow_name";
    private const string WorkflowCallLifecycleMetadataKey = WorkflowCallMetadataPrefix + "lifecycle";
    private const string WorkflowCallChildActorIdMetadataKey = WorkflowCallMetadataPrefix + "child_actor_id";
    private const string WorkflowCallChildRunIdMetadataKey = WorkflowCallMetadataPrefix + "child_run_id";
    private const string WorkflowCallParentRunIdMetadataKey = WorkflowCallMetadataPrefix + "parent_run_id";
    private const string WorkflowCallParentStepIdMetadataKey = WorkflowCallMetadataPrefix + "parent_step_id";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly Func<string> _ownerActorIdAccessor;
    private readonly Func<ILogger> _loggerAccessor;
    private readonly Func<IMessage, CancellationToken, Task> _persistDomainEventAsync;
    private readonly Func<IReadOnlyList<IMessage>, CancellationToken, Task> _persistDomainEventsAsync;
    private readonly Func<IMessage, TopologyAudience, CancellationToken, Task> _publishAsync;
    private readonly Func<string, IMessage, CancellationToken, Task> _sendToAsync;
    private readonly Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> _scheduleSelfTimeoutAsync;
    private readonly Func<RuntimeCallbackLease, CancellationToken, Task> _cancelDurableCallbackAsync;

    public SubWorkflowOrchestrator(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        Func<string> ownerActorIdAccessor,
        Func<ILogger> loggerAccessor,
        Func<IMessage, CancellationToken, Task> persistDomainEventAsync,
        Func<IReadOnlyList<IMessage>, CancellationToken, Task> persistDomainEventsAsync,
        Func<IMessage, TopologyAudience, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>> scheduleSelfTimeoutAsync,
        Func<RuntimeCallbackLease, CancellationToken, Task> cancelDurableCallbackAsync)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _ownerActorIdAccessor = ownerActorIdAccessor ?? throw new ArgumentNullException(nameof(ownerActorIdAccessor));
        _loggerAccessor = loggerAccessor ?? throw new ArgumentNullException(nameof(loggerAccessor));
        _persistDomainEventAsync = persistDomainEventAsync ?? throw new ArgumentNullException(nameof(persistDomainEventAsync));
        _persistDomainEventsAsync = persistDomainEventsAsync ?? throw new ArgumentNullException(nameof(persistDomainEventsAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _sendToAsync = sendToAsync ?? throw new ArgumentNullException(nameof(sendToAsync));
        _scheduleSelfTimeoutAsync = scheduleSelfTimeoutAsync ?? throw new ArgumentNullException(nameof(scheduleSelfTimeoutAsync));
        _cancelDurableCallbackAsync = cancelDurableCallbackAsync ?? throw new ArgumentNullException(nameof(cancelDurableCallbackAsync));
    }

    public async Task HandleInvokeRequestedAsync(
        SubWorkflowInvokeRequestedEvent request,
        WorkflowRunState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);

        var parentRunId = WorkflowRunIdNormalizer.Normalize(request.ParentRunId);
        var parentStepId = request.ParentStepId?.Trim() ?? string.Empty;
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(request.WorkflowName);
        if (string.IsNullOrWhiteSpace(parentStepId))
        {
            _loggerAccessor().LogWarning(
                "workflow_call invocation failed: missing parent step id. parentRun={ParentRunId}",
                parentRunId);
            await PublishWorkflowCallFailureAsync(parentStepId, parentRunId, "workflow_call missing parent_step_id", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(workflowName))
        {
            await PublishWorkflowCallFailureAsync(parentStepId, parentRunId, "workflow_call missing workflow parameter", ct);
            return;
        }

        if (!WorkflowCallLifecycle.IsSupported(request.Lifecycle))
        {
            await PublishWorkflowCallFailureAsync(
                parentStepId,
                parentRunId,
                $"workflow_call lifecycle must be {WorkflowCallLifecycle.AllowedValuesText}",
                ct);
            return;
        }

        var lifecycle = WorkflowCallLifecycle.Normalize(request.Lifecycle);
        var invocationId = string.IsNullOrWhiteSpace(request.InvocationId)
            ? WorkflowCallInvocationIdFactory.Build(parentRunId, parentStepId)
            : request.InvocationId.Trim();
        RuntimeCallbackLease? timeoutLease = null;

        try
        {
            var inlineSnapshot = TryResolveInlineWorkflowDefinitionSnapshot(workflowName, state);
            if (inlineSnapshot != null)
            {
                await StartSubWorkflowAsync(
                    invocationId,
                    parentRunId,
                    parentStepId,
                    request.Input ?? string.Empty,
                    lifecycle,
                    inlineSnapshot,
                    state,
                    ct);
                return;
            }

            var definitionActorId = BuildDefinitionActorId(workflowName);
            var timeoutMs = DefaultDefinitionResolutionTimeoutMs;
            var timeoutCallbackId = BuildDefinitionResolutionTimeoutCallbackId(invocationId);
            timeoutLease = await _scheduleSelfTimeoutAsync(
                timeoutCallbackId,
                TimeSpan.FromMilliseconds(timeoutMs),
                new SubWorkflowDefinitionResolutionTimeoutFiredEvent
                {
                    InvocationId = invocationId,
                    ParentRunId = parentRunId,
                    ParentStepId = parentStepId,
                    WorkflowName = workflowName,
                    DefinitionActorId = definitionActorId,
                    TimeoutMs = timeoutMs,
                },
                ct);

            await _persistDomainEventAsync(new SubWorkflowDefinitionResolutionRegisteredEvent
            {
                InvocationId = invocationId,
                ParentRunId = parentRunId,
                ParentStepId = parentStepId,
                WorkflowName = workflowName,
                DefinitionActorId = definitionActorId,
                Input = request.Input ?? string.Empty,
                Lifecycle = lifecycle,
                TimeoutCallbackId = timeoutCallbackId,
                TimeoutCallbackGeneration = timeoutLease.Generation,
                TimeoutCallbackActorId = timeoutLease.ActorId,
                TimeoutCallbackBackend = timeoutLease.Backend switch
                {
                    RuntimeCallbackBackend.Dedicated => (int)WorkflowRuntimeCallbackBackendState.Dedicated,
                    _ => (int)WorkflowRuntimeCallbackBackendState.InMemory,
                },
                TimeoutMs = timeoutMs,
            }, ct);

            await _sendToAsync(
                definitionActorId,
                new SubWorkflowDefinitionResolveRequestedEvent
                {
                    InvocationId = invocationId,
                    ParentActorId = _ownerActorIdAccessor(),
                    ParentRunId = parentRunId,
                    ParentStepId = parentStepId,
                    WorkflowName = workflowName,
                    Lifecycle = lifecycle,
                    RequestedDefinitionActorId = definitionActorId,
                },
                ct);
        }
        catch (Exception ex)
        {
            _loggerAccessor().LogWarning(
                ex,
                "workflow_call failed: workflow={WorkflowName} parentRun={ParentRunId} parentStep={ParentStepId}",
                workflowName,
                parentRunId,
                parentStepId);
            await _persistDomainEventsAsync(
                [
                    new SubWorkflowDefinitionResolutionClearedEvent
                    {
                        InvocationId = invocationId,
                    },
                ],
                ct);
            await TryCancelDefinitionResolutionTimeoutAsync(timeoutLease, CancellationToken.None);
            await PublishWorkflowCallFailureAsync(
                parentStepId,
                parentRunId,
                $"workflow_call invocation failed: {ex.Message}",
                ct);
        }
    }

    public async Task HandleDefinitionResolutionTimeoutFiredAsync(
        SubWorkflowDefinitionResolutionTimeoutFiredEvent timeout,
        EventEnvelope? inboundEnvelope,
        WorkflowRunState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        ArgumentNullException.ThrowIfNull(state);

        if (!TryGetPendingDefinitionResolution(state, timeout.InvocationId, out var pending))
            return;

        if (!MatchesDefinitionResolutionTimeout(inboundEnvelope, pending))
        {
            _loggerAccessor().LogDebug(
                "Ignore workflow_call definition timeout without matching lease. invocation={InvocationId}",
                timeout.InvocationId);
            return;
        }

        await _persistDomainEventsAsync(
            [
                timeout,
                new SubWorkflowDefinitionResolutionClearedEvent
                {
                    InvocationId = pending.InvocationId,
                },
            ],
            ct);

            await PublishWorkflowCallFailureAsync(
                pending.ParentStepId,
                pending.ParentRunId,
                $"workflow_call timed out waiting for definition resolution after {timeout.TimeoutMs}ms.",
                ct);
    }

    public async Task CancelPendingDefinitionResolutionTimeoutsAsync(
        WorkflowRunState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (var pending in state.PendingSubWorkflowDefinitionResolutions)
            await TryCancelDefinitionResolutionTimeoutAsync(pending, ct);
    }

    public async Task HandleDefinitionResolvedAsync(
        SubWorkflowDefinitionResolvedEvent resolved,
        WorkflowRunState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(state);

        if (!TryGetPendingDefinitionResolution(state, resolved.InvocationId, out var pending))
            return;

        await _persistDomainEventAsync(resolved, ct);

        var definition = resolved.Definition;
        if (definition == null)
        {
            await HandleDefinitionResolveFailureAsync(
                pending,
                "workflow_call definition reply did not include a definition snapshot.",
                ct);
            return;
        }

        var resolvedDefinitionActorId = definition.DefinitionActorId?.Trim() ?? string.Empty;
        if (!string.Equals(
                pending.DefinitionActorId,
                resolvedDefinitionActorId,
                StringComparison.Ordinal))
        {
            await HandleDefinitionResolveFailureAsync(
                pending,
                $"workflow_call definition reply actor mismatch. expected '{pending.DefinitionActorId}', got '{resolvedDefinitionActorId}'.",
                ct);
            return;
        }

        try
        {
            await StartSubWorkflowAsync(
                pending.InvocationId,
                pending.ParentRunId,
                pending.ParentStepId,
                pending.Input ?? string.Empty,
                pending.Lifecycle,
                definition,
                state,
                ct);
            await TryCancelDefinitionResolutionTimeoutAsync(pending, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _loggerAccessor().LogWarning(
                ex,
                "workflow_call resolved definition failed to start child workflow. invocation={InvocationId} workflow={WorkflowName}",
                pending.InvocationId,
                pending.WorkflowName);
            await HandleDefinitionResolveFailureAsync(
                pending,
                $"workflow_call invocation failed: {ex.Message}",
                ct);
        }
    }

    public async Task HandleDefinitionResolveFailedAsync(
        SubWorkflowDefinitionResolveFailedEvent failed,
        WorkflowRunState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(failed);
        ArgumentNullException.ThrowIfNull(state);

        if (!TryGetPendingDefinitionResolution(state, failed.InvocationId, out var pending))
            return;

        await _persistDomainEventAsync(failed, ct);

        var error = string.IsNullOrWhiteSpace(failed.Error)
            ? $"workflow_call failed to resolve workflow '{pending.WorkflowName}'."
            : failed.Error;
        await HandleDefinitionResolveFailureAsync(pending, error, ct);
    }

    private async Task HandleDefinitionResolveFailureAsync(
        WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution pending,
        string error,
        CancellationToken ct)
    {
        await _persistDomainEventsAsync(
            [
                new SubWorkflowDefinitionResolutionClearedEvent
                {
                    InvocationId = pending.InvocationId,
                },
            ],
            ct);
        await PublishWorkflowCallFailureAsync(
            pending.ParentStepId,
            pending.ParentRunId,
            error,
            ct);
        await TryCancelDefinitionResolutionTimeoutAsync(pending, CancellationToken.None);
    }

    private async Task StartSubWorkflowAsync(
        string invocationId,
        string parentRunId,
        string parentStepId,
        string input,
        string lifecycle,
        WorkflowDefinitionSnapshot definition,
        WorkflowRunState state,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentStepId);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ValidateDefinitionSnapshotOrThrow(definition);

        var childRunId = invocationId;
        var childActor = await ResolveOrCreateSubWorkflowActorAsync(definition, lifecycle, state, childRunId, ct);

        await _persistDomainEventAsync(new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = invocationId,
            ParentRunId = parentRunId,
            ParentStepId = parentStepId,
            WorkflowName = definition.WorkflowName ?? string.Empty,
            ChildActorId = childActor.Id,
            ChildRunId = childRunId,
            Lifecycle = lifecycle,
            DefinitionActorId = definition.DefinitionActorId ?? string.Empty,
            DefinitionVersion = definition.DefinitionVersion,
        }, ct);

        var start = new StartWorkflowEvent
        {
            WorkflowName = definition.WorkflowName ?? string.Empty,
            Input = input ?? string.Empty,
            RunId = childRunId,
        };
        start.Parameters[WorkflowCallInvocationIdMetadataKey] = invocationId;
        start.Parameters[WorkflowCallParentRunIdMetadataKey] = parentRunId;
        start.Parameters[WorkflowCallParentStepIdMetadataKey] = parentStepId;
        start.Parameters[WorkflowCallWorkflowNameMetadataKey] = definition.WorkflowName ?? string.Empty;
        start.Parameters[WorkflowCallLifecycleMetadataKey] = lifecycle;

        try
        {
            await _sendToAsync(childActor.Id, start, ct);
        }
        catch (Exception ex)
        {
            await _persistDomainEventAsync(
                new SubWorkflowInvocationCompletedEvent
                {
                    InvocationId = invocationId,
                    ChildRunId = childRunId,
                    Success = false,
                    Error = $"workflow_call failed to dispatch StartWorkflowEvent: {ex.Message}",
                },
                ct);
            await TryFinalizeNonSingletonChildAsync(
                new WorkflowRunState.Types.PendingSubWorkflowInvocation
                {
                    InvocationId = invocationId,
                    ParentRunId = parentRunId,
                    ParentStepId = parentStepId,
                    WorkflowName = definition.WorkflowName ?? string.Empty,
                    ChildActorId = childActor.Id,
                    ChildRunId = childRunId,
                    Lifecycle = lifecycle,
                    DefinitionActorId = definition.DefinitionActorId ?? string.Empty,
                    DefinitionVersion = definition.DefinitionVersion,
                },
                ct);
            throw;
        }
    }

    public async Task<bool> TryHandleCompletionAsync(
        WorkflowCompletedEvent completed,
        string? publisherActorId,
        WorkflowRunState state,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(state);

        var childRunId = completed.RunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(childRunId))
            return false;

        if (!TryGetPendingInvocationByChildRunId(state, childRunId, out var pending))
            return false;

        var expectedChildActorId = pending.ChildActorId?.Trim() ?? string.Empty;
        var completionPublisherId = publisherActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(expectedChildActorId) &&
            !string.Equals(expectedChildActorId, completionPublisherId, StringComparison.Ordinal))
        {
            _loggerAccessor().LogWarning(
                "Ignore workflow_call completion due to publisher mismatch. childRun={ChildRunId} expectedPublisher={ExpectedPublisherId} actualPublisher={ActualPublisherId}",
                childRunId,
                expectedChildActorId,
                completionPublisherId);
            return true;
        }

        if (string.IsNullOrWhiteSpace(expectedChildActorId))
        {
            _loggerAccessor().LogWarning(
                "workflow_call completion matched by child run only because child actor id is missing. childRun={ChildRunId} publisher={PublisherId}",
                childRunId,
                completionPublisherId);
        }

        await _persistDomainEventAsync(new SubWorkflowInvocationCompletedEvent
        {
            InvocationId = pending.InvocationId,
            ChildRunId = childRunId,
            Success = completed.Success,
            Output = completed.Output ?? string.Empty,
            Error = completed.Error ?? string.Empty,
        }, ct);

        var parentCompleted = new StepCompletedEvent
        {
            StepId = pending.ParentStepId,
            RunId = pending.ParentRunId,
            Success = completed.Success,
            Output = completed.Output,
            Error = completed.Error,
        };
        parentCompleted.Annotations[WorkflowCallInvocationIdMetadataKey] = pending.InvocationId;
        parentCompleted.Annotations[WorkflowCallWorkflowNameMetadataKey] = pending.WorkflowName;
        parentCompleted.Annotations[WorkflowCallLifecycleMetadataKey] = WorkflowCallLifecycle.Normalize(pending.Lifecycle);
        parentCompleted.Annotations[WorkflowCallChildActorIdMetadataKey] = pending.ChildActorId;
        parentCompleted.Annotations[WorkflowCallChildRunIdMetadataKey] = childRunId;

        await _publishAsync(parentCompleted, TopologyAudience.Self, ct);
        await TryFinalizeNonSingletonChildAsync(pending, ct);
        return true;
    }

    public async Task CleanupPendingInvocationsForRunAsync(string runId, WorkflowRunState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        if (string.IsNullOrWhiteSpace(normalizedRunId))
            return;

        var staleInvocations = CollectPendingInvocationsByParentRunId(state, normalizedRunId);
        var staleDefinitionResolutions = state.PendingSubWorkflowDefinitionResolutions
            .Where(x => string.Equals(x.ParentRunId, normalizedRunId, StringComparison.Ordinal))
            .Select(x => x.InvocationId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Select(x => (IMessage)new SubWorkflowDefinitionResolutionClearedEvent
            {
                InvocationId = x,
            });
        var cleanupEvents = staleInvocations
            .Select(pending => (IMessage)new SubWorkflowInvocationCompletedEvent
            {
                InvocationId = pending.InvocationId,
                ChildRunId = pending.ChildRunId,
                Success = false,
                Error = "parent workflow completed before child workflow completion",
            })
            .Concat(staleDefinitionResolutions)
            .ToList();

        if (cleanupEvents.Count == 0)
            return;

        await _persistDomainEventsAsync(cleanupEvents, ct);

        foreach (var staleInvocation in staleInvocations)
            await TryFinalizeNonSingletonChildAsync(staleInvocation, ct);
    }

    public static WorkflowRunState ApplySubWorkflowBindingUpserted(WorkflowRunState current, SubWorkflowBindingUpsertedEvent evt)
    {
        var next = current.Clone();
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(evt.WorkflowName);
        var childActorId = evt.ChildActorId?.Trim() ?? string.Empty;
        var definitionActorId = evt.DefinitionActorId?.Trim() ?? string.Empty;
        if ((string.IsNullOrWhiteSpace(workflowName) && string.IsNullOrWhiteSpace(definitionActorId)) ||
            string.IsNullOrWhiteSpace(childActorId))
            return next;

        var lifecycle = WorkflowCallLifecycle.Normalize(evt.Lifecycle);
        for (var i = 0; i < next.SubWorkflowBindings.Count; i++)
        {
            var existing = next.SubWorkflowBindings[i];
            if (!BindingMatches(existing, workflowName, definitionActorId, lifecycle))
                continue;

            next.SubWorkflowBindings[i] = new WorkflowRunState.Types.SubWorkflowBinding
            {
                WorkflowName = workflowName,
                ChildActorId = childActorId,
                Lifecycle = lifecycle,
                DefinitionActorId = definitionActorId,
                DefinitionVersion = evt.DefinitionVersion,
            };
            return next;
        }

        next.SubWorkflowBindings.Add(new WorkflowRunState.Types.SubWorkflowBinding
        {
            WorkflowName = workflowName,
            ChildActorId = childActorId,
            Lifecycle = lifecycle,
            DefinitionActorId = definitionActorId,
            DefinitionVersion = evt.DefinitionVersion,
        });
        return next;
    }

    public static WorkflowRunState ApplySubWorkflowDefinitionResolutionRegistered(
        WorkflowRunState current,
        SubWorkflowDefinitionResolutionRegisteredEvent evt)
    {
        var next = current.Clone();
        var invocationId = evt.InvocationId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(invocationId))
            return next;

        var pending = new WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution
        {
            InvocationId = invocationId,
            ParentRunId = WorkflowRunIdNormalizer.Normalize(evt.ParentRunId),
            ParentStepId = evt.ParentStepId?.Trim() ?? string.Empty,
            WorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(evt.WorkflowName),
            DefinitionActorId = evt.DefinitionActorId?.Trim() ?? string.Empty,
            Input = evt.Input ?? string.Empty,
            Lifecycle = WorkflowCallLifecycle.Normalize(evt.Lifecycle),
            TimeoutLease = string.IsNullOrWhiteSpace(evt.TimeoutCallbackId)
                ? null
                : new WorkflowRuntimeCallbackLeaseState
                {
                    ActorId = evt.TimeoutCallbackActorId?.Trim() ?? string.Empty,
                    CallbackId = evt.TimeoutCallbackId?.Trim() ?? string.Empty,
                    Generation = evt.TimeoutCallbackGeneration,
                    Backend = evt.TimeoutCallbackBackend == (int)WorkflowRuntimeCallbackBackendState.Dedicated
                        ? WorkflowRuntimeCallbackBackendState.Dedicated
                        : WorkflowRuntimeCallbackBackendState.InMemory,
                },
            TimeoutCallbackId = evt.TimeoutCallbackId?.Trim() ?? string.Empty,
            TimeoutMs = evt.TimeoutMs,
        };

        RemovePendingDefinitionResolution(next, invocationId);
        AddPendingDefinitionResolution(next, pending);
        return next;
    }

    public static WorkflowRunState ApplySubWorkflowDefinitionResolutionCleared(
        WorkflowRunState current,
        SubWorkflowDefinitionResolutionClearedEvent evt)
    {
        var next = current.Clone();
        RemovePendingDefinitionResolution(next, evt.InvocationId?.Trim() ?? string.Empty);
        return next;
    }

    public static WorkflowRunState ApplySubWorkflowInvocationRegistered(WorkflowRunState current, SubWorkflowInvocationRegisteredEvent evt)
    {
        var next = current.Clone();
        var invocationId = evt.InvocationId?.Trim() ?? string.Empty;
        var childRunId = evt.ChildRunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(invocationId) || string.IsNullOrWhiteSpace(childRunId))
            return next;

        var pending = new WorkflowRunState.Types.PendingSubWorkflowInvocation
        {
            InvocationId = invocationId,
            ParentRunId = WorkflowRunIdNormalizer.Normalize(evt.ParentRunId),
            ParentStepId = evt.ParentStepId?.Trim() ?? string.Empty,
            WorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(evt.WorkflowName),
            ChildActorId = evt.ChildActorId?.Trim() ?? string.Empty,
            ChildRunId = childRunId,
            Lifecycle = WorkflowCallLifecycle.Normalize(evt.Lifecycle),
            DefinitionActorId = evt.DefinitionActorId?.Trim() ?? string.Empty,
            DefinitionVersion = evt.DefinitionVersion,
        };
        RemovePendingDefinitionResolution(next, invocationId);
        RemovePendingInvocation(next, invocationId, childRunId);
        AddPendingInvocation(next, pending);
        return next;
    }

    public static WorkflowRunState ApplySubWorkflowInvocationCompleted(WorkflowRunState current, SubWorkflowInvocationCompletedEvent evt)
    {
        var next = current.Clone();
        RemovePendingInvocation(
            next,
            evt.InvocationId?.Trim() ?? string.Empty,
            evt.ChildRunId?.Trim() ?? string.Empty);
        return next;
    }

    public static void PruneIdleSubWorkflowBindings(WorkflowRunState state, WorkflowDefinition workflow)
    {
        if (state.SubWorkflowBindings.Count == 0)
            return;

        var referencedSingletonWorkflows = CollectReferencedSingletonWorkflowNames(workflow.Steps);
        for (var i = state.SubWorkflowBindings.Count - 1; i >= 0; i--)
        {
            if (ShouldEvictBinding(state, state.SubWorkflowBindings[i], referencedSingletonWorkflows))
                state.SubWorkflowBindings.RemoveAt(i);
        }
    }

    private async Task<IActor> ResolveOrCreateSubWorkflowActorAsync(
        WorkflowDefinitionSnapshot definition,
        string lifecycle,
        WorkflowRunState state,
        string childRunId,
        CancellationToken ct)
    {
        var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(definition.WorkflowName);
        var definitionActorId = definition.DefinitionActorId?.Trim() ?? string.Empty;
        var normalizedLifecycle = WorkflowCallLifecycle.Normalize(lifecycle);
        if (!string.Equals(normalizedLifecycle, WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase))
        {
            return await CreateSubWorkflowActorAsync(
                definition,
                normalizedLifecycle,
                state,
                childRunId,
                persistBinding: false,
                ct);
        }

        var existingBinding = state.SubWorkflowBindings.FirstOrDefault(x =>
            BindingMatches(x, normalizedWorkflowName, definitionActorId, normalizedLifecycle));

        if (existingBinding != null && !string.IsNullOrWhiteSpace(existingBinding.ChildActorId))
        {
            var existingActorId = existingBinding.ChildActorId.Trim();
            if (await _runtime.ExistsAsync(existingActorId))
            {
                var existingActor = await _runtime.GetAsync(existingActorId);
                if (existingActor != null)
                {
                    if (!BindingVersionMatches(existingBinding, definition))
                    {
                        await BindSubWorkflowActorAsync(existingActor.Id, definition, childRunId, state, ct);
                        await PersistBindingUpsertedAsync(
                            normalizedWorkflowName,
                            existingActor.Id,
                            normalizedLifecycle,
                            definitionActorId,
                            definition.DefinitionVersion,
                            ct);
                    }

                    return existingActor;
                }
            }
        }

        return await CreateSubWorkflowActorAsync(
            definition,
            normalizedLifecycle,
            state,
            childRunId,
            persistBinding: true,
            ct);
    }

    private async Task<IActor> CreateSubWorkflowActorAsync(
        WorkflowDefinitionSnapshot definition,
        string lifecycle,
        WorkflowRunState state,
        string childRunId,
        bool persistBinding,
        CancellationToken ct)
    {
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(definition.WorkflowName);
        var workflowYaml = definition.WorkflowYaml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workflowYaml))
            throw new InvalidOperationException($"workflow_call references unregistered workflow '{workflowName}'");

        var childActorId = BuildSubWorkflowActorId(definition, lifecycle);
        var childActor = await ResolveOrCreateWorkflowActorByIdAsync(childActorId);
        await _runtime.LinkAsync(_ownerActorIdAccessor(), childActor.Id);
        await BindSubWorkflowActorAsync(childActor.Id, definition, childRunId, state, ct);

        if (persistBinding)
        {
            await PersistBindingUpsertedAsync(
                workflowName,
                childActor.Id,
                lifecycle,
                definition.DefinitionActorId ?? string.Empty,
                definition.DefinitionVersion,
                ct);
        }

        return childActor;
    }

    private Task BindSubWorkflowActorAsync(
        string actorId,
        WorkflowDefinitionSnapshot definition,
        string runId,
        WorkflowRunState state,
        CancellationToken ct)
    {
        return _dispatchPort.DispatchAsync(
            actorId,
            CreateWorkflowRunBindEnvelope(definition, runId, state),
            ct);
    }

    private async Task PersistBindingUpsertedAsync(
        string workflowName,
        string childActorId,
        string lifecycle,
        string definitionActorId,
        int definitionVersion,
        CancellationToken ct)
    {
        await _persistDomainEventAsync(new SubWorkflowBindingUpsertedEvent
        {
            WorkflowName = workflowName,
            ChildActorId = childActorId,
            Lifecycle = lifecycle,
            DefinitionActorId = definitionActorId,
            DefinitionVersion = definitionVersion,
        }, ct);
    }

    private static WorkflowDefinitionSnapshot? TryResolveInlineWorkflowDefinitionSnapshot(
        string workflowName,
        WorkflowRunState state)
    {
        if (state.InlineWorkflowYamls.Count == 0)
            return null;

        foreach (var (registeredName, yaml) in state.InlineWorkflowYamls)
        {
            if (!string.Equals(registeredName, workflowName, StringComparison.OrdinalIgnoreCase))
                continue;

            var snapshot = new WorkflowDefinitionSnapshot
            {
                DefinitionActorId = string.Empty,
                WorkflowName = workflowName,
                WorkflowYaml = yaml,
                ScopeId = state.ScopeId ?? string.Empty,
                DefinitionVersion = 0,
            };

            foreach (var (inlineWorkflowName, inlineWorkflowYaml) in state.InlineWorkflowYamls)
                snapshot.InlineWorkflowYamls[inlineWorkflowName] = inlineWorkflowYaml;

            return snapshot;
        }

        return null;
    }

    private static void ValidateDefinitionSnapshotOrThrow(WorkflowDefinitionSnapshot definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(definition.WorkflowName);
        var sourceDescription = string.IsNullOrWhiteSpace(definition.DefinitionActorId)
            ? $"inline workflow '{normalizedWorkflowName}'"
            : $"workflow '{normalizedWorkflowName}' from definition actor '{definition.DefinitionActorId.Trim()}'";
        if (string.IsNullOrWhiteSpace(normalizedWorkflowName))
            throw new InvalidOperationException("workflow_call definition snapshot is missing workflow_name.");

        var workflowYaml = definition.WorkflowYaml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workflowYaml))
            throw new InvalidOperationException($"workflow_call {sourceDescription} YAML is empty.");

        try
        {
            var workflow = DefinitionParser.Parse(workflowYaml);
            var errors = WorkflowValidator.Validate(
                workflow,
                new WorkflowValidator.WorkflowValidationOptions
                {
                    RequireKnownStepTypes = false,
                },
                availableWorkflowNames: null);
            if (errors.Count > 0)
                throw new InvalidOperationException($"workflow_call {sourceDescription} is invalid: {string.Join("; ", errors)}");

            if (!string.Equals(
                    WorkflowRunIdNormalizer.NormalizeWorkflowName(workflow.Name),
                    normalizedWorkflowName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"workflow_call definition snapshot name mismatch. expected '{normalizedWorkflowName}', got '{workflow.Name}'.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException invalidOperation ||
                                   !invalidOperation.Message.StartsWith("workflow_call ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"workflow_call {sourceDescription} is invalid: {ex.Message}", ex);
        }
    }

    private async Task PublishWorkflowCallFailureAsync(
        string parentStepId,
        string parentRunId,
        string error,
        CancellationToken ct)
    {
        await _publishAsync(new StepCompletedEvent
        {
            StepId = parentStepId ?? string.Empty,
            RunId = parentRunId ?? string.Empty,
            Success = false,
            Error = error ?? "workflow_call invocation failed",
        }, TopologyAudience.Self, ct);
    }

    private async Task TryFinalizeNonSingletonChildAsync(
        WorkflowRunState.Types.PendingSubWorkflowInvocation pending,
        CancellationToken ct)
    {
        if (string.Equals(
                WorkflowCallLifecycle.Normalize(pending.Lifecycle),
                WorkflowCallLifecycle.Singleton,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(pending.ChildActorId))
            return;

        try
        {
            await _runtime.UnlinkAsync(pending.ChildActorId, ct);
            await _runtime.DestroyAsync(pending.ChildActorId, ct);
        }
        catch (Exception ex)
        {
            _loggerAccessor().LogWarning(
                ex,
                "Ignore non-singleton child cleanup failure for actor {ChildActorId}.",
                pending.ChildActorId);
        }
    }

    private async Task TryCancelDefinitionResolutionTimeoutAsync(
        WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution pending,
        CancellationToken ct)
    {
        await TryCancelDefinitionResolutionTimeoutAsync(
            WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(pending.TimeoutLease),
            ct);
    }

    private async Task TryCancelDefinitionResolutionTimeoutAsync(
        RuntimeCallbackLease? lease,
        CancellationToken ct)
    {
        if (lease == null)
            return;

        try
        {
            await _cancelDurableCallbackAsync(lease, ct);
        }
        catch (Exception ex)
        {
            _loggerAccessor().LogDebug(
                ex,
                "Ignore workflow_call definition timeout cancellation failure. callback={CallbackId} generation={Generation}",
                lease.CallbackId,
                lease.Generation);
        }
    }

    private string BuildSubWorkflowActorId(WorkflowDefinitionSnapshot definition, string lifecycle)
    {
        var stableBindingKey = string.IsNullOrWhiteSpace(definition.DefinitionActorId)
            ? WorkflowRunIdNormalizer.NormalizeWorkflowName(definition.WorkflowName)
            : definition.DefinitionActorId.Trim();
        var workflowSegment = SanitizeActorSegment(stableBindingKey);
        if (!string.Equals(lifecycle, WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase))
            return $"{_ownerActorIdAccessor()}:workflow:{workflowSegment}:{Guid.NewGuid():N}";

        return $"{_ownerActorIdAccessor()}:workflow:{workflowSegment}";
    }

    private async Task<IActor> ResolveOrCreateWorkflowActorByIdAsync(string actorId)
    {
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return existing;

        try
        {
            return await _runtime.CreateAsync(typeof(WorkflowRunGAgent), actorId);
        }
        catch (Exception ex)
        {
            var raced = await _runtime.GetAsync(actorId);
            if (raced != null)
            {
                _loggerAccessor().LogDebug(
                    ex,
                    "Ignore create race for workflow child actor {ActorId}; using existing actor instance.",
                    actorId);
                return raced;
            }

            throw new InvalidOperationException(
                $"workflow_call failed to create or get sub-workflow actor '{actorId}'.",
                ex);
        }
    }

    private EventEnvelope CreateWorkflowRunBindEnvelope(
        WorkflowDefinitionSnapshot definition,
        string runId,
        WorkflowRunState state)
    {
        var inlineWorkflowYamls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (definition.InlineWorkflowYamls.Count > 0)
        {
            foreach (var (key, value) in definition.InlineWorkflowYamls)
                inlineWorkflowYamls[key] = value;
        }
        else
        {
            foreach (var (key, value) in state.InlineWorkflowYamls)
                inlineWorkflowYamls[key] = value;
        }

        var bindDefinition = new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = definition.DefinitionActorId ?? string.Empty,
            WorkflowYaml = definition.WorkflowYaml ?? string.Empty,
            WorkflowName = definition.WorkflowName ?? string.Empty,
            RunId = runId ?? string.Empty,
            ScopeId = string.IsNullOrWhiteSpace(definition.ScopeId)
                ? state.ScopeId ?? string.Empty
                : definition.ScopeId,
            InlineWorkflowYamls = { inlineWorkflowYamls },
        };

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(bindDefinition),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(_ownerActorIdAccessor(), TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };
    }

    private static bool ShouldEvictBinding(
        WorkflowRunState state,
        WorkflowRunState.Types.SubWorkflowBinding binding,
        ISet<string> referencedSingletonWorkflows)
    {
        var lifecycle = WorkflowCallLifecycle.Normalize(binding.Lifecycle);
        if (!string.Equals(lifecycle, WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase))
            return true;

        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(binding.WorkflowName);
        if (string.IsNullOrWhiteSpace(workflowName))
            return true;
        if (referencedSingletonWorkflows.Contains(workflowName))
            return false;

        var childActorId = binding.ChildActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(childActorId) &&
            HasPendingInvocationForChildActor(state.PendingSubWorkflowInvocations, childActorId))
        {
            return false;
        }

        return true;
    }

    private static bool BindingMatches(
        WorkflowRunState.Types.SubWorkflowBinding binding,
        string workflowName,
        string definitionActorId,
        string lifecycle)
    {
        if (!string.Equals(WorkflowCallLifecycle.Normalize(binding.Lifecycle), lifecycle, StringComparison.OrdinalIgnoreCase))
            return false;

        var bindingDefinitionActorId = binding.DefinitionActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(definitionActorId) ||
            !string.IsNullOrWhiteSpace(bindingDefinitionActorId))
        {
            return string.Equals(bindingDefinitionActorId, definitionActorId, StringComparison.Ordinal);
        }

        return string.Equals(binding.WorkflowName, workflowName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool BindingVersionMatches(
        WorkflowRunState.Types.SubWorkflowBinding binding,
        WorkflowDefinitionSnapshot definition)
    {
        var definitionActorId = definition.DefinitionActorId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(definitionActorId))
            return true;

        return binding.DefinitionVersion == definition.DefinitionVersion;
    }

    private static string BuildDefinitionActorId(string workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
            throw new ArgumentException("Workflow name is required.", nameof(workflowName));

        return "workflow-definition:" + workflowName.Trim().ToLowerInvariant();
    }

    private string BuildDefinitionResolutionTimeoutCallbackId(string invocationId)
    {
        return RuntimeCallbackKeyComposer.BuildCallbackId(
            "workflow-definition-resolution-timeout",
            _ownerActorIdAccessor(),
            invocationId);
    }

    private static bool MatchesDefinitionResolutionTimeout(
        EventEnvelope? inboundEnvelope,
        WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution pending)
    {
        if (inboundEnvelope == null)
            return false;

        if (pending.TimeoutLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(inboundEnvelope, pending.TimeoutLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(inboundEnvelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, pending.TimeoutCallbackId, StringComparison.Ordinal);
    }

    private static bool HasPendingInvocationForChildActor(
        IEnumerable<WorkflowRunState.Types.PendingSubWorkflowInvocation> pendingInvocations,
        string childActorId)
    {
        return pendingInvocations.Any(x =>
            string.Equals(x.ChildActorId, childActorId, StringComparison.Ordinal));
    }

    private static ISet<string> CollectReferencedSingletonWorkflowNames(IEnumerable<StepDefinition> steps)
    {
        var workflows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectReferencedSingletonWorkflowNamesRecursive(steps, workflows);
        return workflows;
    }

    private static void CollectReferencedSingletonWorkflowNamesRecursive(
        IEnumerable<StepDefinition> steps,
        ISet<string> workflows)
    {
        foreach (var step in steps)
        {
            var canonicalStepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
            if (string.Equals(canonicalStepType, "workflow_call", StringComparison.OrdinalIgnoreCase))
            {
                var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(step.Parameters.GetValueOrDefault("workflow", string.Empty));
                var lifecycle = WorkflowCallLifecycle.Normalize(step.Parameters.GetValueOrDefault("lifecycle", string.Empty));
                if (!string.IsNullOrWhiteSpace(workflowName) &&
                    string.Equals(lifecycle, WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase))
                {
                    workflows.Add(workflowName);
                }
            }

            if (step.Children is { Count: > 0 })
                CollectReferencedSingletonWorkflowNamesRecursive(step.Children, workflows);
        }
    }

    private static string SanitizeActorSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "workflow";

        var trimmed = value.Trim();
        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                span[i] = char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-';
            }
        });
    }

    private static List<WorkflowRunState.Types.PendingSubWorkflowInvocation> CollectPendingInvocationsByParentRunId(
        WorkflowRunState state,
        string parentRunId)
    {
        var pendingByRun = new List<WorkflowRunState.Types.PendingSubWorkflowInvocation>();
        var indexedChildRunIds = new HashSet<string>(StringComparer.Ordinal);
        if (state.PendingChildRunIdsByParentRunId.TryGetValue(parentRunId, out var childRunIdSet) &&
            childRunIdSet.ChildRunIds.Count > 0)
        {
            foreach (var childRunId in childRunIdSet.ChildRunIds)
            {
                if (TryGetPendingInvocationByChildRunId(state, childRunId, out var pending))
                {
                    pendingByRun.Add(pending);
                    indexedChildRunIds.Add(pending.ChildRunId);
                }
            }
        }

        foreach (var pending in state.PendingSubWorkflowInvocations)
        {
            if (!string.Equals(pending.ParentRunId, parentRunId, StringComparison.Ordinal))
                continue;

            if (indexedChildRunIds.Contains(pending.ChildRunId))
                continue;

            pendingByRun.Add(pending);
            indexedChildRunIds.Add(pending.ChildRunId);
        }

        return pendingByRun;
    }

    private static bool TryGetPendingInvocationByChildRunId(
        WorkflowRunState state,
        string childRunId,
        out WorkflowRunState.Types.PendingSubWorkflowInvocation pending)
    {
        if (TryGetPendingInvocationIndexByChildRunId(state, childRunId, out var index))
        {
            pending = state.PendingSubWorkflowInvocations[index];
            return true;
        }

        pending = null!;
        return false;
    }

    private static bool TryGetPendingDefinitionResolution(
        WorkflowRunState state,
        string invocationId,
        out WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution pending)
    {
        if (TryGetPendingDefinitionResolutionIndex(state, invocationId, out var index))
        {
            pending = state.PendingSubWorkflowDefinitionResolutions[index];
            return true;
        }

        pending = null!;
        return false;
    }

    private static bool TryGetPendingDefinitionResolutionIndex(
        WorkflowRunState state,
        string invocationId,
        out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(invocationId))
            return false;

        if (state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.TryGetValue(invocationId, out var mappedIndex) &&
            mappedIndex >= 0 &&
            mappedIndex < state.PendingSubWorkflowDefinitionResolutions.Count &&
            string.Equals(
                state.PendingSubWorkflowDefinitionResolutions[mappedIndex].InvocationId,
                invocationId,
                StringComparison.Ordinal))
        {
            index = mappedIndex;
            return true;
        }

        for (var i = 0; i < state.PendingSubWorkflowDefinitionResolutions.Count; i++)
        {
            if (!string.Equals(state.PendingSubWorkflowDefinitionResolutions[i].InvocationId, invocationId, StringComparison.Ordinal))
                continue;

            index = i;
            return true;
        }

        return false;
    }

    private static bool TryGetPendingInvocationIndexByChildRunId(
        WorkflowRunState state,
        string childRunId,
        out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(childRunId))
            return false;

        if (state.PendingSubWorkflowInvocationIndexByChildRunId.TryGetValue(childRunId, out var mappedIndex) &&
            mappedIndex >= 0 &&
            mappedIndex < state.PendingSubWorkflowInvocations.Count &&
            string.Equals(state.PendingSubWorkflowInvocations[mappedIndex].ChildRunId, childRunId, StringComparison.Ordinal))
        {
            index = mappedIndex;
            return true;
        }

        for (var i = 0; i < state.PendingSubWorkflowInvocations.Count; i++)
        {
            if (!string.Equals(state.PendingSubWorkflowInvocations[i].ChildRunId, childRunId, StringComparison.Ordinal))
                continue;

            index = i;
            return true;
        }

        return false;
    }

    private static void AddPendingInvocation(
        WorkflowRunState state,
        WorkflowRunState.Types.PendingSubWorkflowInvocation pending)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pending);

        state.PendingSubWorkflowInvocations.Add(pending);
        var addedIndex = state.PendingSubWorkflowInvocations.Count - 1;
        var childRunId = pending.ChildRunId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(childRunId))
            state.PendingSubWorkflowInvocationIndexByChildRunId[childRunId] = addedIndex;

        AddChildRunIdToParentIndex(
            state.PendingChildRunIdsByParentRunId,
            pending.ParentRunId,
            childRunId);
    }

    private static void AddPendingDefinitionResolution(
        WorkflowRunState state,
        WorkflowRunState.Types.PendingSubWorkflowDefinitionResolution pending)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pending);

        state.PendingSubWorkflowDefinitionResolutions.Add(pending);
        var addedIndex = state.PendingSubWorkflowDefinitionResolutions.Count - 1;
        var invocationId = pending.InvocationId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(invocationId))
            state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId[invocationId] = addedIndex;
    }

    private static void RemovePendingInvocation(
        WorkflowRunState state,
        string invocationId,
        string childRunId)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!string.IsNullOrWhiteSpace(childRunId))
        {
            while (TryGetPendingInvocationIndexByChildRunId(state, childRunId, out var index))
                RemovePendingInvocationAt(state, index);
        }

        if (string.IsNullOrWhiteSpace(invocationId))
            return;

        var scanIndex = 0;
        while (scanIndex < state.PendingSubWorkflowInvocations.Count)
        {
            var pending = state.PendingSubWorkflowInvocations[scanIndex];
            if (!string.Equals(pending.InvocationId, invocationId, StringComparison.Ordinal))
            {
                scanIndex++;
                continue;
            }

            RemovePendingInvocationAt(state, scanIndex);
        }
    }

    private static void RemovePendingDefinitionResolution(
        WorkflowRunState state,
        string invocationId)
    {
        ArgumentNullException.ThrowIfNull(state);

        while (TryGetPendingDefinitionResolutionIndex(state, invocationId, out var index))
            RemovePendingDefinitionResolutionAt(state, index);
    }

    private static void RemovePendingInvocationAt(WorkflowRunState state, int index)
    {
        var pendingInvocations = state.PendingSubWorkflowInvocations;
        if (index < 0 || index >= pendingInvocations.Count)
            return;

        var removed = pendingInvocations[index];
        var removedChildRunId = removed.ChildRunId?.Trim() ?? string.Empty;
        var removedParentRunId = removed.ParentRunId?.Trim() ?? string.Empty;
        var lastIndex = pendingInvocations.Count - 1;
        var movedTailChildRunId = string.Empty;
        var movedTail = false;

        if (index != lastIndex)
        {
            var tail = pendingInvocations[lastIndex];
            pendingInvocations[index] = tail;
            pendingInvocations.RemoveAt(lastIndex);

            movedTail = true;
            movedTailChildRunId = tail.ChildRunId?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(movedTailChildRunId))
                state.PendingSubWorkflowInvocationIndexByChildRunId[movedTailChildRunId] = index;
        }
        else
        {
            pendingInvocations.RemoveAt(lastIndex);
        }

        if (!string.IsNullOrWhiteSpace(removedChildRunId) &&
            (!movedTail || !string.Equals(removedChildRunId, movedTailChildRunId, StringComparison.Ordinal)))
        {
            state.PendingSubWorkflowInvocationIndexByChildRunId.Remove(removedChildRunId);
        }

        RemoveChildRunIdFromParentIndex(
            state.PendingChildRunIdsByParentRunId,
            removedParentRunId,
            removedChildRunId);
    }

    private static void RemovePendingDefinitionResolutionAt(WorkflowRunState state, int index)
    {
        var pendingResolutions = state.PendingSubWorkflowDefinitionResolutions;
        if (index < 0 || index >= pendingResolutions.Count)
            return;

        var removed = pendingResolutions[index];
        var removedInvocationId = removed.InvocationId?.Trim() ?? string.Empty;
        var lastIndex = pendingResolutions.Count - 1;
        var movedTailInvocationId = string.Empty;
        var movedTail = false;

        if (index != lastIndex)
        {
            var tail = pendingResolutions[lastIndex];
            pendingResolutions[index] = tail;
            pendingResolutions.RemoveAt(lastIndex);

            movedTail = true;
            movedTailInvocationId = tail.InvocationId?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(movedTailInvocationId))
            {
                state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId[movedTailInvocationId] = index;
            }
        }
        else
        {
            pendingResolutions.RemoveAt(lastIndex);
        }

        if (!string.IsNullOrWhiteSpace(removedInvocationId) &&
            (!movedTail || !string.Equals(removedInvocationId, movedTailInvocationId, StringComparison.Ordinal)))
        {
            state.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Remove(removedInvocationId);
        }
    }

    private static void AddChildRunIdToParentIndex(
        MapField<string, WorkflowRunState.Types.ChildRunIdSet> parentIndex,
        string parentRunId,
        string childRunId)
    {
        var normalizedParentRunId = WorkflowRunIdNormalizer.Normalize(parentRunId);
        var normalizedChildRunId = childRunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedParentRunId) ||
            string.IsNullOrWhiteSpace(normalizedChildRunId))
        {
            return;
        }

        if (!parentIndex.TryGetValue(normalizedParentRunId, out var childRuns))
        {
            childRuns = new WorkflowRunState.Types.ChildRunIdSet();
            parentIndex[normalizedParentRunId] = childRuns;
        }

        if (!childRuns.ChildRunIds.Contains(normalizedChildRunId))
            childRuns.ChildRunIds.Add(normalizedChildRunId);
    }

    private static void RemoveChildRunIdFromParentIndex(
        MapField<string, WorkflowRunState.Types.ChildRunIdSet> parentIndex,
        string parentRunId,
        string childRunId)
    {
        var normalizedParentRunId = WorkflowRunIdNormalizer.Normalize(parentRunId);
        var normalizedChildRunId = childRunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedParentRunId) ||
            string.IsNullOrWhiteSpace(normalizedChildRunId) ||
            !parentIndex.TryGetValue(normalizedParentRunId, out var childRuns))
        {
            return;
        }

        for (var i = childRuns.ChildRunIds.Count - 1; i >= 0; i--)
        {
            if (string.Equals(childRuns.ChildRunIds[i], normalizedChildRunId, StringComparison.Ordinal))
                childRuns.ChildRunIds.RemoveAt(i);
        }

        if (childRuns.ChildRunIds.Count == 0)
            parentIndex.Remove(normalizedParentRunId);
    }
}
