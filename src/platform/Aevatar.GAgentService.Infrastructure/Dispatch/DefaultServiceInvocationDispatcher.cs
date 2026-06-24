using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Dispatch;

public sealed class DefaultServiceInvocationDispatcher : IServiceInvocationDispatcher
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IScriptRuntimeCommandPort _scriptRuntimeCommandPort;
    private readonly IWorkflowRunProvisioningPort _workflowRunProvisioningPort;
    private readonly IServiceRunRegistrationPort _serviceRunRegistrationPort;

    public DefaultServiceInvocationDispatcher(
        IActorDispatchPort dispatchPort,
        IScriptRuntimeCommandPort scriptRuntimeCommandPort,
        IWorkflowRunProvisioningPort workflowRunProvisioningPort,
        IServiceRunRegistrationPort serviceRunRegistrationPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scriptRuntimeCommandPort = scriptRuntimeCommandPort ?? throw new ArgumentNullException(nameof(scriptRuntimeCommandPort));
        _workflowRunProvisioningPort = workflowRunProvisioningPort ?? throw new ArgumentNullException(nameof(workflowRunProvisioningPort));
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
        return CreateReceipt(target, target.Service.PrimaryActorId, commandId, correlationId, runId);
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
        return CreateReceipt(target, target.Service.PrimaryActorId, commandId, correlationId, runId);
    }

    private async Task<ServiceInvocationAcceptedReceipt> DispatchWorkflowAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        CancellationToken ct)
    {
        var chatRequest = request.Payload?.Unpack<ChatRequestEvent>()
            ?? throw new InvalidOperationException("Workflow services require ChatRequestEvent payload.");
        var plan = target.Artifact.DeploymentPlan.WorkflowPlan;
        var definitionActorId = ResolveWorkflowServiceDefinitionActorId(target, plan);
        var run = await _workflowRunProvisioningPort.CreateRunAsync(
            new WorkflowDefinitionBinding(
                definitionActorId,
                plan.WorkflowName,
                plan.WorkflowYaml,
                plan.InlineWorkflowYamls,
                ResolveAuthoritativeScopeId(request, chatRequest),
                string.IsNullOrWhiteSpace(request.RunOrigin)
                    ? WorkflowRunOrigins.ServiceInvoke
                    : request.RunOrigin.Trim()),
            ct);
        var commandId = ResolveCommandId(request);
        var correlationId = ResolveCorrelationId(request, commandId);
        var runId = ResolveRunId(request, commandId);
        await RegisterRunAsync(target, request, runId, commandId, correlationId, run.ActorId, ServiceImplementationKind.Workflow, ct);
        var envelope = CreateEnvelope(run.ActorId, Any.Pack(ToWorkflowChatRequest(chatRequest)), commandId, correlationId);
        await _dispatchPort.DispatchAsync(run.ActorId, envelope, ct);
        return CreateReceipt(target, run.ActorId, commandId, correlationId, runId);
    }

    private static string ResolveWorkflowServiceDefinitionActorId(
        ServiceInvocationResolvedTarget target,
        WorkflowServiceDeploymentPlan plan)
    {
        var serviceDefinitionActorId = target.Service.PrimaryActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(serviceDefinitionActorId))
            return serviceDefinitionActorId;

        return plan.DefinitionActorId?.Trim() ?? string.Empty;
    }

    private static WorkflowChatRequestEvent ToWorkflowChatRequest(ChatRequestEvent source)
    {
        var request = new WorkflowChatRequestEvent
        {
            Prompt = source.Prompt ?? string.Empty,
            SessionId = source.SessionId ?? string.Empty,
            TimeoutMs = source.TimeoutMs,
            ScopeId = source.ScopeId ?? string.Empty,
            CallerCredential = BuildWorkflowCallerCredential(source.ConnectorHttpAuthorization),
        };
        foreach (var part in source.InputParts)
        {
            request.InputParts.Add(new WorkflowChatInputPartPayload
            {
                Kind = part.Kind switch
                {
                    ChatContentPartKind.Text => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Text,
                    ChatContentPartKind.Image => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Image,
                    ChatContentPartKind.Audio => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Audio,
                    ChatContentPartKind.Video => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Video,
                    _ => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Unspecified,
                },
                Text = part.Text ?? string.Empty,
                DataBase64 = part.DataBase64 ?? string.Empty,
                MediaType = part.MediaType ?? string.Empty,
                Uri = part.Uri ?? string.Empty,
                Name = part.Name ?? string.Empty,
            });
        }
        foreach (var (key, value) in source.Headers)
            request.Headers[key] = value;
        foreach (var (key, value) in source.Metadata)
            request.Metadata[key] = value;
        request.LlmControl = new WorkflowLlmControlContext
        {
            ModelOverride = source.LlmControl?.ModelOverride ?? string.Empty,
            UserMemoryPrompt = source.LlmControl?.UserMemoryPrompt ?? string.Empty,
            SenderNyxIdAccessToken = source.LlmControl?.SenderNyxIdAccessToken ?? string.Empty,
        };
        if (source.LlmControl?.HasMaxToolRoundsOverride == true)
            request.LlmControl.MaxToolRoundsOverride = source.LlmControl.MaxToolRoundsOverride;
        return request;
    }

    private static Aevatar.Workflow.Abstractions.WorkflowCallerCredential BuildWorkflowCallerCredential(string? connectorHttpAuthorization)
    {
        if (string.IsNullOrWhiteSpace(connectorHttpAuthorization))
            return new Aevatar.Workflow.Abstractions.WorkflowCallerCredential();

        const string bearerPrefix = "Bearer ";
        var authorization = connectorHttpAuthorization.Trim();
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Connector HTTP authorization must use the Bearer scheme.", nameof(connectorHttpAuthorization));

        var parsed = WorkflowCallerCredentialTokens.ParseOptional(authorization[bearerPrefix.Length..]);
        if (parsed.IsInvalid)
            throw new ArgumentException("Connector HTTP authorization bearer token is invalid.", nameof(connectorHttpAuthorization));

        return new Aevatar.Workflow.Abstractions.WorkflowCallerCredential
        {
            BearerToken = parsed.NormalizedBearerToken ?? string.Empty,
        };
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
            ScheduleId = request.ScheduleId ?? string.Empty,
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
        string correlationId,
        string runId)
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
            RunId = runId,
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

        // Refactor (issue1543): Old pattern: Headers/Metadata scope fallback could override workflow binding authority.  New principle: only typed identity or typed chat payload scope can bind a workflow run.
        if (!string.IsNullOrWhiteSpace(chatRequest.ScopeId))
            return chatRequest.ScopeId.Trim();

        // 06-23-observatory-run-coverage-filter (W3b): fail fast instead of returning an empty scope, which
        // would materialize an unattributed run invisible to every scope-bound observatory viewer. Scope is a
        // cross-run authoritative fact; a workflow service invoke must carry it via typed identity or payload.
        throw new InvalidOperationException(
            "Workflow service invocation requires a scope: neither service identity tenantId nor chat payload " +
            "scopeId was provided. Refusing to create an unattributed (empty-scope) run.");
    }
}
