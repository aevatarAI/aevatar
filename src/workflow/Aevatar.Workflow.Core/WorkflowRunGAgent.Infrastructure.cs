using System.Globalization;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    private Task EnsureAgentTreeAsync(CancellationToken ct) =>
        _effectDispatcher.EnsureAgentTreeAsync(ct);

    private Task ScheduleWorkflowCallbackAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        int semanticGeneration,
        string stepId,
        string? sessionId,
        string kind,
        CancellationToken ct) =>
        _effectDispatcher.ScheduleWorkflowCallbackAsync(
            callbackId,
            dueTime,
            evt,
            semanticGeneration,
            stepId,
            sessionId,
            kind,
            ct);

    private Task<IActor> ResolveOrCreateSubWorkflowRunActorAsync(string actorId, CancellationToken ct) =>
        _effectDispatcher.ResolveOrCreateSubWorkflowRunActorAsync(actorId, ct);

    private Task<string> ResolveWorkflowYamlAsync(string workflowName, CancellationToken ct) =>
        _effectDispatcher.ResolveWorkflowYamlAsync(workflowName, ct);

    private EventEnvelope CreateWorkflowDefinitionBindEnvelope(string workflowYaml, string workflowName) =>
        _effectDispatcher.CreateWorkflowDefinitionBindEnvelope(workflowYaml, workflowName);

    private EventEnvelope CreateRoleAgentInitializeEnvelope(RoleDefinition role) =>
        _effectDispatcher.CreateRoleAgentInitializeEnvelope(role);

    private Task LogWarningAsync(Exception? ex, string message, object?[] args)
    {
        if (ex == null)
            Logger.LogWarning(message, args);
        else
            Logger.LogWarning(ex, message, args);

        return Task.CompletedTask;
    }

    private async Task<string> ResolveWorkflowYamlCoreAsync(string workflowName, CancellationToken ct)
    {
        foreach (var (registeredName, yaml) in State.InlineWorkflowYamls)
        {
            if (string.Equals(registeredName, workflowName, StringComparison.OrdinalIgnoreCase))
                return yaml;
        }

        var resolver = _workflowDefinitionResolver ?? Services?.GetService<IWorkflowDefinitionResolver>();
        if (resolver == null)
            throw new InvalidOperationException("workflow_call requires IWorkflowDefinitionResolver service registration.");

        var yamlFromResolver = await resolver.GetWorkflowYamlAsync(workflowName, ct);
        if (string.IsNullOrWhiteSpace(yamlFromResolver))
            throw new InvalidOperationException($"workflow_call references unregistered workflow '{workflowName}'");
        return yamlFromResolver;
    }

    private EventEnvelope CreateWorkflowDefinitionBindEnvelopeCore(string workflowYaml, string workflowName)
    {
        var bind = new BindWorkflowDefinitionEvent
        {
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
        };
        foreach (var (name, yaml) in State.InlineWorkflowYamls)
            bind.InlineWorkflowYamls[name] = yaml;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(bind),
            PublisherId = Id,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    private EventEnvelope CreateRoleAgentInitializeEnvelopeCore(RoleDefinition role)
    {
        var initialize = new InitializeRoleAgentEvent
        {
            RoleName = role.Name ?? string.Empty,
            ProviderName = string.IsNullOrWhiteSpace(role.Provider) ? string.Empty : role.Provider,
            Model = string.IsNullOrWhiteSpace(role.Model) ? string.Empty : role.Model,
            SystemPrompt = role.SystemPrompt ?? string.Empty,
            MaxTokens = role.MaxTokens ?? 0,
            MaxToolRounds = role.MaxToolRounds ?? 0,
            MaxHistoryMessages = role.MaxHistoryMessages ?? 0,
            StreamBufferCapacity = role.StreamBufferCapacity ?? 0,
        };
        if (role.Temperature.HasValue)
            initialize.Temperature = role.Temperature.Value;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(initialize),
            PublisherId = Id,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    private async Task RemovePendingLlmCallAndPublishFailureAsync(
        string sessionId,
        string stepId,
        string runId,
        string error,
        CancellationToken ct)
    {
        var next = State.Clone();
        next.PendingLlmCalls.Remove(sessionId);
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = stepId,
            RunId = runId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);
    }

    private async Task<bool> TryHandleRegisteredPrimitiveAsync(StepRequestEvent request, CancellationToken ct)
    {
        if (Services == null ||
            !_primitiveRegistry.TryCreate(request.StepType, Services, out var handler) ||
            handler == null)
        {
            return false;
        }

        try
        {
            await handler.HandleAsync(
                request,
                new WorkflowPrimitiveExecutionContext(
                    Id,
                    Services,
                    Logger,
                    new HashSet<string>(_knownStepTypes, StringComparer.OrdinalIgnoreCase),
                    new PrimitiveEventSink(this)),
                ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Workflow primitive {PrimitiveName} failed", handler.Name);
            await PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = $"primitive '{handler.Name}' failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }

        return true;
    }

    private sealed class PrimitiveEventSink(WorkflowRunGAgent owner) : WorkflowPrimitiveExecutionContext.IWorkflowPrimitiveEventSink
    {
        public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction, CancellationToken ct)
            where TEvent : IMessage =>
            owner.PublishAsync(evt, direction, ct);
    }
}
