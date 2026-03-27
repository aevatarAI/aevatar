using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Serialization;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public static class ScopeServiceEndpoints
{
    public static IEndpointRouteBuilder MapScopeServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("ScopeServices");
        group.MapPost("/{scopeId}/draft-run", HandleDraftRunAsync);
        group.MapPut("/{scopeId}/binding", HandleUpsertBindingAsync);
        group.MapGet("/{scopeId}/binding", HandleGetBindingAsync);
        group.MapPost("/{scopeId}/binding/revisions/{revisionId}:activate", HandleActivateBindingRevisionAsync);
        group.MapPost("/{scopeId}/invoke/chat:stream", HandleInvokeDefaultChatStreamAsync);
        group.MapPost("/{scopeId}/invoke/{endpointId}", HandleInvokeDefaultAsync);
        group.MapPost("/{scopeId}/runs/{runId}:resume", HandleResumeDefaultRunAsync);
        group.MapPost("/{scopeId}/runs/{runId}:signal", HandleSignalDefaultRunAsync);
        group.MapPost("/{scopeId}/runs/{runId}:stop", HandleStopDefaultRunAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/invoke/{endpointId}:stream", HandleInvokeStreamAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/invoke/{endpointId}", HandleInvokeAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:resume", HandleResumeRunAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:signal", HandleSignalRunAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/runs/{runId}:stop", HandleStopRunAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/bindings", HandleCreateBindingAsync);
        group.MapPut("/{scopeId}/services/{serviceId}/bindings/{bindingId}", HandleUpdateBindingAsync);
        group.MapPost("/{scopeId}/services/{serviceId}/bindings/{bindingId}:retire", HandleRetireBindingAsync);
        group.MapGet("/{scopeId}/services/{serviceId}/bindings", HandleGetBindingsAsync);
        return app;
    }

    private static async Task HandleDraftRunAsync(
        HttpContext http,
        string scopeId,
        ScopeDraftRunHttpRequest request,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        CancellationToken ct)
    {
        try
        {
            if (await ScopeEndpointAccess.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            if (request.WorkflowYamls == null || request.WorkflowYamls.Count == 0)
                throw new InvalidOperationException("workflowYamls is required.");

            var scopedHeaders = BuildScopedHeaders(scopeId, request.Headers);
            await WorkflowCapabilityEndpoints.HandleChat(
                http,
                new ChatInput
                {
                    Prompt = request.Prompt?.Trim() ?? string.Empty,
                    WorkflowYamls = request.WorkflowYamls,
                    SessionId = request.SessionId,
                    ScopeId = scopeId,
                    Metadata = scopedHeaders,
                },
                chatRunService,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_SCOPE_DRAFT_RUN_REQUEST",
                ex.Message,
                ct);
        }
    }

    private static async Task<IResult> HandleUpsertBindingAsync(
        HttpContext http,
        string scopeId,
        UpsertScopeBindingHttpRequest request,
        [FromServices] IScopeBindingCommandPort commandPort,
        CancellationToken ct)
    {
        try
        {
            if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var result = await commandPort.UpsertAsync(
                new ScopeBindingUpsertRequest(
                    scopeId,
                    ParseScopeBindingImplementationKind(request.ImplementationKind),
                    ToWorkflowSpec(request),
                    request.Script == null
                        ? null
                        : new ScopeBindingScriptSpec(
                            request.Script.ScriptId,
                            request.Script.ScriptRevision),
                    request.GAgent == null
                        ? null
                        : new ScopeBindingGAgentSpec(
                            request.GAgent.ActorTypeName,
                            request.GAgent.PreferredActorId,
                            (request.GAgent.Endpoints ?? [])
                            .Select(endpoint => new ScopeBindingGAgentEndpoint(
                                endpoint.EndpointId,
                                endpoint.DisplayName,
                                ParseEndpointKind(endpoint.Kind),
                                endpoint.RequestTypeUrl,
                                endpoint.ResponseTypeUrl,
                                endpoint.Description))
                            .ToArray()),
                    request.DisplayName,
                    request.RevisionId),
                ct);
            return Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_BINDING_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static async Task<IResult> HandleGetBindingAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceServingQueryPort servingQueryPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
        var identity = BuildScopeServiceIdentity(
            options.Value,
            normalizedScopeId,
            ResolveDefaultScopeServiceId(options.Value));
        var service = await lifecycleQueryPort.GetServiceAsync(identity, ct);
        if (service == null)
        {
            return Results.Ok(new ScopeBindingStatusHttpResponse(
                false,
                normalizedScopeId,
                identity.ServiceId,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                []));
        }

        var revisions = await lifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
        var servingSet = await servingQueryPort.GetServiceServingSetAsync(identity, ct);
        var servingTargetsByRevision = BuildServingTargetIndex(servingSet);
        var revisionSnapshots = (revisions?.Revisions ?? [])
            .Select(revision =>
            {
                servingTargetsByRevision.TryGetValue(revision.RevisionId, out var servingTarget);
                return new ScopeBindingRevisionHttpResponse(
                    revision.RevisionId,
                    revision.ImplementationKind,
                    revision.Status,
                    revision.ArtifactHash,
                    revision.FailureReason,
                    string.Equals(service.DefaultServingRevisionId, revision.RevisionId, StringComparison.Ordinal),
                    string.Equals(service.ActiveServingRevisionId, revision.RevisionId, StringComparison.Ordinal),
                    servingTarget != null,
                    servingTarget?.AllocationWeight ?? 0,
                    servingTarget?.ServingState ?? string.Empty,
                    servingTarget?.DeploymentId ?? string.Empty,
                    servingTarget?.PrimaryActorId ?? string.Empty,
                    revision.CreatedAt,
                    revision.PreparedAt,
                    revision.PublishedAt,
                    revision.RetiredAt);
            })
            .OrderByDescending(x => x.IsDefaultServing)
            .ThenByDescending(x => x.IsActiveServing)
            .ThenByDescending(x => x.PublishedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue)
            .ToArray();

        return Results.Ok(new ScopeBindingStatusHttpResponse(
            true,
            normalizedScopeId,
            service.ServiceId,
            service.DisplayName,
            service.ServiceKey,
            service.DefaultServingRevisionId,
            service.ActiveServingRevisionId,
            service.DeploymentId,
            service.DeploymentStatus,
            service.PrimaryActorId,
            service.UpdatedAt,
            revisionSnapshots));
    }

    private static async Task<IResult> HandleActivateBindingRevisionAsync(
        HttpContext http,
        string scopeId,
        string revisionId,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IServiceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
                return denied;

            var normalizedScopeId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId));
            var normalizedRevisionId = ScopeWorkflowCapabilityOptions.NormalizeRequired(revisionId, nameof(revisionId));
            var identity = BuildScopeServiceIdentity(
                options.Value,
                normalizedScopeId,
                ResolveDefaultScopeServiceId(options.Value));
            var service = await lifecycleQueryPort.GetServiceAsync(identity, ct);
            if (service == null)
            {
                return Results.NotFound(new
                {
                    code = "SCOPE_BINDING_NOT_FOUND",
                    message = $"Scope '{normalizedScopeId}' has no active binding.",
                });
            }

            var revisions = await lifecycleQueryPort.GetServiceRevisionsAsync(identity, ct);
            var revision = revisions?.Revisions.FirstOrDefault(x =>
                string.Equals(x.RevisionId, normalizedRevisionId, StringComparison.Ordinal));
            if (revision == null)
            {
                return Results.NotFound(new
                {
                    code = "SCOPE_BINDING_REVISION_NOT_FOUND",
                    message = $"Revision '{normalizedRevisionId}' was not found for scope '{normalizedScopeId}'.",
                });
            }

            if (string.Equals(revision.Status, ServiceRevisionStatus.Retired.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new
                {
                    code = "SCOPE_BINDING_REVISION_RETIRED",
                    message = $"Revision '{normalizedRevisionId}' is retired and cannot be activated.",
                });
            }

            await commandPort.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
            {
                Identity = identity.Clone(),
                RevisionId = normalizedRevisionId,
            }, ct);
            await commandPort.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
            {
                Identity = identity.Clone(),
                RevisionId = normalizedRevisionId,
            }, ct);

            return Results.Ok(new ScopeBindingActivationHttpResponse(
                normalizedScopeId,
                identity.ServiceId,
                service.DisplayName,
                normalizedRevisionId));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new
            {
                code = "INVALID_SCOPE_BINDING_ACTIVATION_REQUEST",
                message = ex.Message,
            });
        }
    }

    private static Task HandleInvokeDefaultChatStreamAsync(
        HttpContext http,
        string scopeId,
        StreamScopeServiceHttpRequest request,
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleInvokeStreamAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            "chat",
            request,
            resolutionService,
            admissionAuthorizer,
            chatRunService,
            options,
            ct);

    private static Task<IResult> HandleInvokeDefaultAsync(
        HttpContext http,
        string scopeId,
        string endpointId,
        InvokeScopeServiceHttpRequest request,
        [FromServices] IServiceInvocationPort invocationPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleInvokeAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            endpointId,
            request,
            invocationPort,
            options,
            ct);

    private static Task<IResult> HandleResumeDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        ResumeScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleResumeRunAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            runId,
            request,
            lifecycleQueryPort,
            workflowRunBindingReader,
            resumeService,
            options,
            ct);

    private static Task<IResult> HandleSignalDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        SignalScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleSignalRunAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            runId,
            request,
            lifecycleQueryPort,
            workflowRunBindingReader,
            signalService,
            options,
            ct);

    private static Task<IResult> HandleStopDefaultRunAsync(
        HttpContext http,
        string scopeId,
        string runId,
        StopScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct) =>
        HandleStopRunAsync(
            http,
            scopeId,
            ResolveDefaultScopeServiceId(options.Value),
            runId,
            request,
            lifecycleQueryPort,
            workflowRunBindingReader,
            stopService,
            options,
            ct);

    private static async Task HandleInvokeStreamAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string endpointId,
        StreamScopeServiceHttpRequest request,
        [FromServices] ServiceInvocationResolutionService resolutionService,
        [FromServices] IInvokeAdmissionAuthorizer admissionAuthorizer,
        [FromServices] ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> chatRunService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        try
        {
            if (await ScopeEndpointAccess.TryWriteScopeAccessDeniedAsync(http, scopeId, ct))
                return;

            var normalizedPrompt = request.Prompt?.Trim() ?? string.Empty;
            var scopedHeaders = BuildScopedHeaders(scopeId, request.Headers);
            var invocationRequest = BuildStreamInvocationRequest(
                options.Value,
                scopeId,
                serviceId,
                endpointId,
                normalizedPrompt,
                scopedHeaders);
            var target = await resolutionService.ResolveAsync(invocationRequest, ct);
            EnsureWorkflowStreamTarget(target, invocationRequest);
            await admissionAuthorizer.AuthorizeAsync(
                target.Service.ServiceKey,
                target.Service.DeploymentId,
                target.Artifact,
                target.Endpoint,
                invocationRequest,
                ct);

            await WorkflowCapabilityEndpoints.HandleChat(
                http,
                new ChatInput
                {
                    Prompt = normalizedPrompt,
                    AgentId = target.Service.PrimaryActorId,
                    SessionId = request.SessionId,
                    ScopeId = scopeId,
                    Metadata = scopedHeaders,
                },
                chatRunService,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonErrorResponseAsync(
                http,
                StatusCodes.Status400BadRequest,
                "INVALID_SERVICE_STREAM_REQUEST",
                ex.Message,
                ct);
        }
    }

    private static async Task<IResult> HandleInvokeAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string endpointId,
        InvokeScopeServiceHttpRequest request,
        [FromServices] IServiceInvocationPort invocationPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var identity = BuildScopeServiceIdentity(options.Value, scopeId, serviceId);
        var receipt = await invocationPort.InvokeAsync(new ServiceInvocationRequest
        {
            Identity = identity,
            EndpointId = endpointId?.Trim() ?? string.Empty,
            CommandId = request.CommandId?.Trim() ?? string.Empty,
            CorrelationId = request.CorrelationId?.Trim() ?? string.Empty,
            Payload = ServiceJsonPayloads.PackBase64(
                request.PayloadTypeUrl?.Trim() ?? string.Empty,
                request.PayloadBase64),
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = string.Empty,
                TenantId = string.Empty,
                AppId = string.Empty,
            },
        }, ct);
        return Results.Accepted($"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}", receipt);
    }

    private static async Task<IResult> HandleResumeRunAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
        ResumeScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> resumeService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceRunAsync(
            http,
            options.Value,
            scopeId,
            serviceId,
            runId,
            request.ActorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        return await WorkflowCapabilityEndpoints.HandleResume(
            new WorkflowResumeInput
            {
                ActorId = resolution.Binding!.ActorId,
                RunId = resolution.Binding.RunId,
                StepId = request.StepId ?? string.Empty,
                CommandId = request.CommandId,
                Approved = request.Approved,
                UserInput = request.UserInput,
                Metadata = request.Metadata,
            },
            resumeService,
            ct);
    }

    private static async Task<IResult> HandleSignalRunAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
        SignalScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> signalService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceRunAsync(
            http,
            options.Value,
            scopeId,
            serviceId,
            runId,
            request.ActorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        return await WorkflowCapabilityEndpoints.HandleSignal(
            new WorkflowSignalInput
            {
                ActorId = resolution.Binding!.ActorId,
                RunId = resolution.Binding.RunId,
                SignalName = request.SignalName ?? string.Empty,
                StepId = request.StepId,
                CommandId = request.CommandId,
                Payload = request.Payload,
            },
            signalService,
            ct);
    }

    private static async Task<IResult> HandleStopRunAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string runId,
        StopScopeServiceRunHttpRequest request,
        [FromServices] IServiceLifecycleQueryPort lifecycleQueryPort,
        [FromServices] IWorkflowRunBindingReader workflowRunBindingReader,
        [FromServices] ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError> stopService,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        var resolution = await ResolveScopeServiceRunAsync(
            http,
            options.Value,
            scopeId,
            serviceId,
            runId,
            request.ActorId,
            lifecycleQueryPort,
            workflowRunBindingReader,
            ct);
        if (resolution.Failure != null)
            return resolution.Failure;

        return await WorkflowCapabilityEndpoints.HandleStop(
            new WorkflowStopInput
            {
                ActorId = resolution.Binding!.ActorId,
                RunId = resolution.Binding.RunId,
                CommandId = request.CommandId,
                Reason = request.Reason,
            },
            stopService,
            ct);
    }

    private static async Task<IResult> HandleCreateBindingAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        ScopeServiceBindingHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var receipt = await commandPort.CreateBindingAsync(new CreateServiceBindingCommand
        {
            Spec = ToBindingSpec(options.Value, scopeId, serviceId, request, request.BindingId ?? string.Empty),
        }, ct);
        return Results.Accepted($"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/bindings/{request.BindingId}", receipt);
    }

    private static async Task<IResult> HandleUpdateBindingAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string bindingId,
        ScopeServiceBindingHttpRequest request,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var receipt = await commandPort.UpdateBindingAsync(new UpdateServiceBindingCommand
        {
            Spec = ToBindingSpec(options.Value, scopeId, serviceId, request, bindingId),
        }, ct);
        return Results.Accepted($"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/bindings/{bindingId}", receipt);
    }

    private static async Task<IResult> HandleRetireBindingAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        string bindingId,
        [FromServices] IServiceGovernanceCommandPort commandPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var receipt = await commandPort.RetireBindingAsync(new RetireServiceBindingCommand
        {
            Identity = BuildScopeServiceIdentity(options.Value, scopeId, serviceId),
            BindingId = bindingId?.Trim() ?? string.Empty,
        }, ct);
        return Results.Accepted($"/api/scopes/{Uri.EscapeDataString(scopeId)}/services/{Uri.EscapeDataString(serviceId)}/bindings/{bindingId}", receipt);
    }

    private static async Task<IResult> HandleGetBindingsAsync(
        HttpContext http,
        string scopeId,
        string serviceId,
        [FromServices] IServiceGovernanceQueryPort queryPort,
        [FromServices] IOptions<ScopeWorkflowCapabilityOptions> options,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var snapshot = await queryPort.GetBindingsAsync(
            BuildScopeServiceIdentity(options.Value, scopeId, serviceId),
            ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static async Task<ScopeServiceRunResolution> ResolveScopeServiceRunAsync(
        HttpContext http,
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string serviceId,
        string runId,
        string? requestedActorId,
        IServiceLifecycleQueryPort lifecycleQueryPort,
        IWorkflowRunBindingReader workflowRunBindingReader,
        CancellationToken ct)
    {
        if (ScopeEndpointAccess.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return new ScopeServiceRunResolution(null, denied);

        var normalizedRunId = ScopeWorkflowCapabilityOptions.NormalizeRequired(runId, nameof(runId));
        var identity = BuildScopeServiceIdentity(options, scopeId, serviceId);
        var service = await lifecycleQueryPort.GetServiceAsync(identity, ct);
        if (service == null)
        {
            return new ScopeServiceRunResolution(
                null,
                Results.NotFound(new
                {
                    code = "SCOPE_SERVICE_NOT_FOUND",
                    message = BuildScopeServiceNotFoundMessage(scopeId, serviceId),
                }));
        }

        var deployments = await lifecycleQueryPort.GetServiceDeploymentsAsync(identity, ct);
        var matches = (await workflowRunBindingReader.ListByRunIdAsync(normalizedRunId, ct: ct))
            .Where(binding => IsRunBoundToScopeService(binding, scopeId, service, deployments))
            .ToList();

        var normalizedActorId = NormalizeOptional(requestedActorId);
        if (!string.IsNullOrWhiteSpace(normalizedActorId))
        {
            matches = matches
                .Where(binding => string.Equals(binding.ActorId, normalizedActorId, StringComparison.Ordinal))
                .ToList();
        }

        if (matches.Count == 0)
        {
            return new ScopeServiceRunResolution(
                null,
                Results.NotFound(new
                {
                    code = "SERVICE_RUN_NOT_FOUND",
                    message = BuildScopeServiceRunNotFoundMessage(scopeId, serviceId, normalizedRunId),
                }));
        }

        if (matches.Count > 1)
        {
            return new ScopeServiceRunResolution(
                null,
                Results.Conflict(new
                {
                    code = "SERVICE_RUN_AMBIGUOUS",
                    message = $"Run '{normalizedRunId}' is ambiguous for service '{serviceId}' in scope '{scopeId}'.",
                }));
        }

        return new ScopeServiceRunResolution(matches[0], null);
    }

    private static ServiceBindingSpec ToBindingSpec(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string serviceId,
        ScopeServiceBindingHttpRequest request,
        string bindingId)
    {
        var spec = new ServiceBindingSpec
        {
            Identity = BuildScopeServiceIdentity(options, scopeId, serviceId),
            BindingId = bindingId?.Trim() ?? string.Empty,
            DisplayName = request.DisplayName?.Trim() ?? string.Empty,
            BindingKind = ParseBindingKind(request.BindingKind),
        };
        spec.PolicyIds.Add(request.PolicyIds ?? []);

        switch (spec.BindingKind)
        {
            case ServiceBindingKind.Service:
                spec.ServiceRef = new BoundServiceRef
                {
                    Identity = BuildScopeServiceIdentity(options, scopeId, request.Service?.ServiceId ?? string.Empty),
                    EndpointId = request.Service?.EndpointId?.Trim() ?? string.Empty,
                };
                break;
            case ServiceBindingKind.Connector:
                spec.ConnectorRef = new BoundConnectorRef
                {
                    ConnectorType = request.Connector?.ConnectorType?.Trim() ?? string.Empty,
                    ConnectorId = request.Connector?.ConnectorId?.Trim() ?? string.Empty,
                };
                break;
            case ServiceBindingKind.Secret:
                spec.SecretRef = new BoundSecretRef
                {
                    SecretName = request.Secret?.SecretName?.Trim() ?? string.Empty,
                };
                break;
            default:
                throw new InvalidOperationException($"Unsupported binding kind '{request.BindingKind}'.");
        }

        return spec;
    }

    private static ServiceBindingKind ParseBindingKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "service" => ServiceBindingKind.Service,
            "connector" => ServiceBindingKind.Connector,
            "secret" => ServiceBindingKind.Secret,
            _ => throw new InvalidOperationException($"Unsupported binding kind '{rawValue}'."),
        };
    }

    private static ServiceEndpointKind ParseEndpointKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "chat" => ServiceEndpointKind.Chat,
            "command" or null or "" => ServiceEndpointKind.Command,
            _ => throw new InvalidOperationException($"Unsupported endpoint kind '{rawValue}'."),
        };
    }

    private static ServiceInvocationRequest BuildStreamInvocationRequest(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string serviceId,
        string endpointId,
        string prompt,
        IReadOnlyDictionary<string, string>? headers)
    {
        var payload = new ChatRequestEvent
        {
            Prompt = prompt,
            ScopeId = scopeId,
        };
        if (headers != null)
        {
            foreach (var (key, value) in headers)
                payload.Metadata[key] = value;
        }

        return new ServiceInvocationRequest
        {
            Identity = BuildScopeServiceIdentity(options, scopeId, serviceId),
            EndpointId = endpointId?.Trim() ?? string.Empty,
            Payload = Any.Pack(payload),
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = string.Empty,
                TenantId = string.Empty,
                AppId = string.Empty,
            },
        };
    }

    private static void EnsureWorkflowStreamTarget(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request)
    {
        if (target.Artifact.ImplementationKind != ServiceImplementationKind.Workflow)
            throw new InvalidOperationException("Only workflow services support SSE stream execution.");
        if (target.Endpoint.Kind != ServiceEndpointKind.Chat)
            throw new InvalidOperationException("Only chat endpoints support SSE stream execution.");
        if (!string.IsNullOrWhiteSpace(target.Endpoint.RequestTypeUrl) &&
            !string.Equals(target.Endpoint.RequestTypeUrl, request.Payload?.TypeUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Endpoint '{target.Endpoint.EndpointId}' expects payload '{target.Endpoint.RequestTypeUrl}', but got '{request.Payload?.TypeUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(target.Service.PrimaryActorId))
            throw new InvalidOperationException("Workflow service has no active definition actor.");
    }

    private static Dictionary<string, string> BuildScopedHeaders(
        string scopeId,
        IReadOnlyDictionary<string, string>? headers)
    {
        var scopedHeaders = headers == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        scopedHeaders.Remove("scope_id");
        scopedHeaders.Remove(WorkflowRunCommandMetadataKeys.ScopeId);
        return scopedHeaders;
    }

    private static bool IsRunBoundToScopeService(
        WorkflowActorBinding binding,
        string scopeId,
        ServiceCatalogSnapshot service,
        ServiceDeploymentCatalogSnapshot? deployments)
    {
        if (binding.ActorKind != WorkflowActorKind.Run ||
            string.IsNullOrWhiteSpace(binding.ActorId) ||
            string.IsNullOrWhiteSpace(binding.EffectiveDefinitionActorId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(binding.ScopeId) &&
            !string.Equals(binding.ScopeId, scopeId, StringComparison.Ordinal))
        {
            return false;
        }

        var definitionActorIds = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(service.PrimaryActorId))
            definitionActorIds.Add(service.PrimaryActorId);
        if (deployments != null)
        {
            foreach (var deployment in deployments.Deployments)
            {
                if (!string.IsNullOrWhiteSpace(deployment.PrimaryActorId))
                    definitionActorIds.Add(deployment.PrimaryActorId);
            }
        }

        return definitionActorIds.Contains(binding.EffectiveDefinitionActorId);
    }

    private static IReadOnlyDictionary<string, ServiceServingTargetSnapshot> BuildServingTargetIndex(
        ServiceServingSetSnapshot? servingSet)
    {
        if (servingSet == null)
            return new Dictionary<string, ServiceServingTargetSnapshot>(StringComparer.Ordinal);

        return servingSet.Targets
            .GroupBy(x => x.RevisionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(x => x.AllocationWeight)
                    .ThenByDescending(x => x.ServingState, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
    }

    private static ServiceIdentity BuildScopeServiceIdentity(
        ScopeWorkflowCapabilityOptions options,
        string scopeId,
        string serviceId)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ServiceIdentity
        {
            TenantId = ScopeWorkflowCapabilityOptions.NormalizeRequired(scopeId, nameof(scopeId)),
            AppId = ScopeWorkflowCapabilityOptions.NormalizeRequired(options.ServiceAppId, nameof(options.ServiceAppId)),
            Namespace = ScopeWorkflowCapabilityOptions.NormalizeRequired(options.ServiceNamespace, nameof(options.ServiceNamespace)),
            ServiceId = ScopeWorkflowCapabilityOptions.NormalizeRequired(serviceId, nameof(serviceId)),
        };
    }

    private static string ResolveDefaultScopeServiceId(ScopeWorkflowCapabilityOptions options) =>
        ScopeWorkflowCapabilityOptions.NormalizeRequired(options.DefaultServiceId, nameof(options.DefaultServiceId));

    private static ScopeBindingImplementationKind ParseScopeBindingImplementationKind(string? rawValue)
    {
        return rawValue?.Trim().ToLowerInvariant() switch
        {
            "workflow" => ScopeBindingImplementationKind.Workflow,
            "script" => ScopeBindingImplementationKind.Scripting,
            "scripting" => ScopeBindingImplementationKind.Scripting,
            "gagent" => ScopeBindingImplementationKind.GAgent,
            _ => throw new InvalidOperationException($"Unsupported implementationKind '{rawValue}'."),
        };
    }

    private static ScopeBindingWorkflowSpec? ToWorkflowSpec(UpsertScopeBindingHttpRequest request)
    {
        var workflowYamls = request.Workflow?.WorkflowYamls ?? request.WorkflowYamls;
        return workflowYamls == null ? null : new ScopeBindingWorkflowSpec(workflowYamls);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string BuildScopeServiceNotFoundMessage(string scopeId, string serviceId) =>
        $"Service '{serviceId}' was not found in scope '{scopeId}'.";

    private static string BuildScopeServiceRunNotFoundMessage(string scopeId, string serviceId, string runId) =>
        $"Run '{runId}' was not found on service '{serviceId}' in scope '{scopeId}'.";

    private static async Task WriteJsonErrorResponseAsync(
        HttpContext http,
        int statusCode,
        string code,
        string message,
        CancellationToken ct)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(new { code, message }, cancellationToken: ct);
    }

    public sealed record InvokeScopeServiceHttpRequest(
        string? CommandId,
        string? CorrelationId,
        string? PayloadTypeUrl,
        string? PayloadBase64);

    public sealed record ScopeDraftRunHttpRequest(
        string Prompt,
        IReadOnlyList<string>? WorkflowYamls,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null);

    public sealed record UpsertScopeBindingHttpRequest(
        string ImplementationKind,
        IReadOnlyList<string>? WorkflowYamls = null,
        ScopeBindingWorkflowHttpRequest? Workflow = null,
        ScopeBindingScriptHttpRequest? Script = null,
        ScopeBindingGAgentHttpRequest? GAgent = null,
        string? DisplayName = null,
        string? RevisionId = null);

    public sealed record ScopeBindingWorkflowHttpRequest(
        IReadOnlyList<string>? WorkflowYamls);

    public sealed record ScopeBindingScriptHttpRequest(
        string ScriptId,
        string? ScriptRevision = null);

    public sealed record ScopeBindingGAgentHttpRequest(
        string ActorTypeName,
        string? PreferredActorId,
        IReadOnlyList<ServiceEndpoints.ServiceEndpointHttpRequest>? Endpoints);

    public sealed record StreamScopeServiceHttpRequest(
        string Prompt,
        string? SessionId = null,
        Dictionary<string, string>? Headers = null);

    public sealed record ResumeScopeServiceRunHttpRequest(
        string? StepId,
        string? CommandId,
        bool Approved,
        string? UserInput = null,
        Dictionary<string, string>? Metadata = null,
        string? ActorId = null);

    public sealed record SignalScopeServiceRunHttpRequest(
        string? SignalName,
        string? StepId = null,
        string? CommandId = null,
        string? Payload = null,
        string? ActorId = null);

    public sealed record StopScopeServiceRunHttpRequest(
        string? Reason = null,
        string? CommandId = null,
        string? ActorId = null);

    public sealed record BoundScopeServiceHttpRequest(
        string ServiceId,
        string? EndpointId = null);

    public sealed record BoundConnectorHttpRequest(
        string ConnectorType,
        string ConnectorId);

    public sealed record BoundSecretHttpRequest(
        string SecretName);

    public sealed record ScopeServiceBindingHttpRequest(
        string? BindingId,
        string? DisplayName,
        string BindingKind,
        BoundScopeServiceHttpRequest? Service,
        BoundConnectorHttpRequest? Connector,
        BoundSecretHttpRequest? Secret,
        IReadOnlyList<string>? PolicyIds = null);

    private sealed record ScopeServiceRunResolution(
        WorkflowActorBinding? Binding,
        IResult? Failure);

    public sealed record ScopeBindingStatusHttpResponse(
        bool Available,
        string ScopeId,
        string ServiceId,
        string DisplayName,
        string ServiceKey,
        string DefaultServingRevisionId,
        string ActiveServingRevisionId,
        string DeploymentId,
        string DeploymentStatus,
        string PrimaryActorId,
        DateTimeOffset? UpdatedAt,
        IReadOnlyList<ScopeBindingRevisionHttpResponse> Revisions);

    public sealed record ScopeBindingRevisionHttpResponse(
        string RevisionId,
        string ImplementationKind,
        string Status,
        string ArtifactHash,
        string FailureReason,
        bool IsDefaultServing,
        bool IsActiveServing,
        bool IsServingTarget,
        int AllocationWeight,
        string ServingState,
        string DeploymentId,
        string PrimaryActorId,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? PreparedAt,
        DateTimeOffset? PublishedAt,
        DateTimeOffset? RetiredAt);

    public sealed record ScopeBindingActivationHttpResponse(
        string ScopeId,
        string ServiceId,
        string DisplayName,
        string RevisionId);
}
