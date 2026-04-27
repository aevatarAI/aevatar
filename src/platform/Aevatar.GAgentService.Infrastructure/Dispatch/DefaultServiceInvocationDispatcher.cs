using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Dispatch;

public sealed class DefaultServiceInvocationDispatcher : IServiceInvocationDispatcher
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IScriptRuntimeCommandPort _scriptRuntimeCommandPort;
    private readonly IWorkflowRunActorPort _workflowRunActorPort;
    private readonly IServiceRunRegistrationPort _serviceRunRegistrationPort;

    public DefaultServiceInvocationDispatcher(
        IActorDispatchPort dispatchPort,
        IScriptRuntimeCommandPort scriptRuntimeCommandPort,
        IWorkflowRunActorPort workflowRunActorPort,
        IServiceRunRegistrationPort serviceRunRegistrationPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scriptRuntimeCommandPort = scriptRuntimeCommandPort ?? throw new ArgumentNullException(nameof(scriptRuntimeCommandPort));
        _workflowRunActorPort = workflowRunActorPort ?? throw new ArgumentNullException(nameof(workflowRunActorPort));
        _serviceRunRegistrationPort = serviceRunRegistrationPort ?? throw new ArgumentNullException(nameof(serviceRunRegistrationPort));
    }

    public async Task<ServiceInvocationAcceptedReceipt> DispatchAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        EnsureEndpointPayloadMatch(target.Endpoint, request);

        return target.Artifact.ImplementationKind switch
        {
            ServiceImplementationKind.Static => await DispatchStaticAsync(target, request, ct),
            ServiceImplementationKind.Scripting => await DispatchScriptingAsync(target, request, ct),
            ServiceImplementationKind.Workflow => await DispatchWorkflowAsync(target, request, ct),
            _ => throw new InvalidOperationException($"Unsupported service implementation '{target.Artifact.ImplementationKind}'."),
        };
    }

    private async Task<ServiceInvocationAcceptedReceipt> DispatchStaticAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        CancellationToken ct)
    {
        var commandId = ResolveCommandId(request);
        var correlationId = ResolveCorrelationId(request, commandId);
        var runId = ResolveRunId(request, commandId);
        await RegisterRunAsync(target, request, runId, commandId, correlationId, target.Service.PrimaryActorId, ServiceImplementationKind.Static, ct);
        var envelope = CreateEnvelope(target.Service.PrimaryActorId, request.Payload, commandId, correlationId);
        await _dispatchPort.DispatchAsync(target.Service.PrimaryActorId, envelope, ct);
        return CreateReceipt(target, target.Service.PrimaryActorId, commandId, correlationId);
    }

    private async Task<ServiceInvocationAcceptedReceipt> DispatchScriptingAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        CancellationToken ct)
    {
        var plan = target.Artifact.DeploymentPlan.ScriptingPlan;
        var commandId = ResolveCommandId(request);
        var correlationId = ResolveCorrelationId(request, commandId);
        var runId = ResolveRunId(request, commandId);
        await RegisterRunAsync(target, request, runId, commandId, correlationId, target.Service.PrimaryActorId, ServiceImplementationKind.Scripting, ct);
        await _scriptRuntimeCommandPort.RunRuntimeAsync(
            target.Service.PrimaryActorId,
            runId: commandId,
            request.Payload?.Clone(),
            plan.Revision,
            plan.DefinitionActorId,
            request.Payload?.TypeUrl ?? string.Empty,
            request.Identity?.TenantId,
            ct);
        return CreateReceipt(target, target.Service.PrimaryActorId, commandId, correlationId);
    }

    private async Task<ServiceInvocationAcceptedReceipt> DispatchWorkflowAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        CancellationToken ct)
    {
        var chatRequest = request.Payload?.Unpack<ChatRequestEvent>()
            ?? throw new InvalidOperationException("Workflow services require ChatRequestEvent payload.");
        var plan = target.Artifact.DeploymentPlan.WorkflowPlan;
        var run = await _workflowRunActorPort.CreateRunAsync(
            new WorkflowDefinitionBinding(
                target.Service.PrimaryActorId,
                plan.WorkflowName,
                plan.WorkflowYaml,
                plan.InlineWorkflowYamls,
                ResolveAuthoritativeScopeId(request, chatRequest)),
            ct);
        var commandId = ResolveCommandId(request);
        var correlationId = ResolveCorrelationId(request, commandId);
        var runId = ResolveRunId(request, commandId);
        await RegisterRunAsync(target, request, runId, commandId, correlationId, run.Actor.Id, ServiceImplementationKind.Workflow, ct);
        var envelope = CreateEnvelope(run.Actor.Id, Any.Pack(chatRequest), commandId, correlationId);
        await _dispatchPort.DispatchAsync(run.Actor.Id, envelope, ct);
        return CreateReceipt(target, run.Actor.Id, commandId, correlationId);
    }

    private async Task RegisterRunAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        string runId,
        string commandId,
        string correlationId,
        string targetActorId,
        ServiceImplementationKind implementationKind,
        CancellationToken ct)
    {
        var record = new ServiceRunRecord
        {
            ScopeId = request.Identity?.TenantId ?? string.Empty,
            ServiceId = request.Identity?.ServiceId ?? string.Empty,
            ServiceKey = target.Service.ServiceKey ?? string.Empty,
            RunId = runId,
            CommandId = commandId,
            CorrelationId = correlationId,
            EndpointId = target.Endpoint.EndpointId ?? string.Empty,
            ImplementationKind = implementationKind,
            TargetActorId = targetActorId ?? string.Empty,
            RevisionId = target.Service.RevisionId ?? string.Empty,
            DeploymentId = target.Service.DeploymentId ?? string.Empty,
            Status = ServiceRunStatus.Accepted,
            Identity = request.Identity?.Clone(),
        };
        await _serviceRunRegistrationPort.RegisterAsync(record, ct);
    }

    private static void EnsureEndpointPayloadMatch(ServiceEndpointDescriptor endpoint, ServiceInvocationRequest request)
    {
        if (request.Payload == null)
            throw new InvalidOperationException("payload is required.");
        if (!string.IsNullOrWhiteSpace(endpoint.RequestTypeUrl) &&
            !string.Equals(endpoint.RequestTypeUrl, request.Payload.TypeUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.EndpointId}' expects payload '{endpoint.RequestTypeUrl}', but got '{request.Payload.TypeUrl}'.");
        }
    }

    private static EventEnvelope CreateEnvelope(
        string actorId,
        Any payload,
        string commandId,
        string correlationId)
    {
        return new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload.Clone(),
            Route = EnvelopeRouteSemantics.CreateDirect("gagent-service.invoke", actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
        };
    }

    private static ServiceInvocationAcceptedReceipt CreateReceipt(
        ServiceInvocationResolvedTarget target,
        string targetActorId,
        string commandId,
        string correlationId)
    {
        return new ServiceInvocationAcceptedReceipt
        {
            RequestId = commandId,
            ServiceKey = target.Service.ServiceKey,
            DeploymentId = target.Service.DeploymentId,
            TargetActorId = targetActorId,
            EndpointId = target.Endpoint.EndpointId,
            CommandId = commandId,
            CorrelationId = correlationId,
        };
    }

    private static string ResolveCommandId(ServiceInvocationRequest request) =>
        string.IsNullOrWhiteSpace(request.CommandId)
            ? Guid.NewGuid().ToString("N")
            : request.CommandId;

    private static string ResolveCorrelationId(ServiceInvocationRequest request, string commandId) =>
        string.IsNullOrWhiteSpace(request.CorrelationId)
            ? commandId
            : request.CorrelationId;

    private static string ResolveRunId(ServiceInvocationRequest request, string commandId) =>
        string.IsNullOrWhiteSpace(request.CommandId)
            ? commandId
            : request.CommandId;

    private static string ResolveAuthoritativeScopeId(ServiceInvocationRequest request, ChatRequestEvent chatRequest)
    {
        if (!string.IsNullOrWhiteSpace(request.Identity?.TenantId))
            return request.Identity.TenantId.Trim();
        return ResolveScopeId(chatRequest);
    }

    private static string ResolveScopeId(ChatRequestEvent chatRequest)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        if (!string.IsNullOrWhiteSpace(chatRequest.ScopeId))
            return chatRequest.ScopeId.Trim();

        return TryResolveScopeId(chatRequest.Headers, out var scopeId) ||
               TryResolveScopeId(chatRequest.Metadata, out scopeId)
            ? scopeId
            : string.Empty;
    }

    private static bool TryResolveScopeId(MapField<string, string> values, out string scopeId)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.TryGetValue(WorkflowRunCommandMetadataKeys.ScopeId, out var workflowScopeId) &&
            !string.IsNullOrWhiteSpace(workflowScopeId))
        {
            scopeId = workflowScopeId.Trim();
            return true;
        }

        if (values.TryGetValue("scope_id", out var legacyScopeId) &&
            !string.IsNullOrWhiteSpace(legacyScopeId))
        {
            scopeId = legacyScopeId.Trim();
            return true;
        }

        scopeId = string.Empty;
        return false;
    }
}
