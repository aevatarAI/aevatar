using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Infrastructure.Dispatch;

public sealed class DefaultServiceInvocationDispatcher : IServiceInvocationDispatcher
{
    private readonly IActorDispatchPort _dispatchPort;
    // Nullable by design: the scripting capability is optional. Hosts composed without it
    // resolve this port to null, and scripting dispatches are rejected in DispatchScriptingAsync.
    private readonly IScriptRuntimeCommandPort? _scriptRuntimeCommandPort;
    private readonly IWorkflowRunProvisioningPort _workflowRunProvisioningPort;
    private readonly IServiceRunRegistrationPort _serviceRunRegistrationPort;
    private readonly IWorkflowArtifactCompatibilityPreflight _artifactPreflight;
    private readonly ILogger<DefaultServiceInvocationDispatcher> _logger;

    public DefaultServiceInvocationDispatcher(
        IActorDispatchPort dispatchPort,
        IScriptRuntimeCommandPort? scriptRuntimeCommandPort,
        IWorkflowRunProvisioningPort workflowRunProvisioningPort,
        IServiceRunRegistrationPort serviceRunRegistrationPort,
        IWorkflowArtifactCompatibilityPreflight artifactPreflight,
        ILogger<DefaultServiceInvocationDispatcher>? logger = null)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _scriptRuntimeCommandPort = scriptRuntimeCommandPort;
        _workflowRunProvisioningPort = workflowRunProvisioningPort ?? throw new ArgumentNullException(nameof(workflowRunProvisioningPort));
        _serviceRunRegistrationPort = serviceRunRegistrationPort ?? throw new ArgumentNullException(nameof(serviceRunRegistrationPort));
        _artifactPreflight = artifactPreflight ?? throw new ArgumentNullException(nameof(artifactPreflight));
        _logger = logger ?? NullLogger<DefaultServiceInvocationDispatcher>.Instance;
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
        EnsureStaticRunContextIsDispatcherOwned(request);
        var registration = await RegisterRunAsync(
            target,
            request,
            runId,
            commandId,
            correlationId,
            target.Service.PrimaryActorId,
            ServiceImplementationKind.Static,
            ct);
        var payload = AttachStaticRunContext(
            request.Payload,
            registration.RunActorId,
            runId,
            commandId,
            correlationId,
            request.ServiceRunCompletionNotificationTarget?.ExpiresAtUnixMs ?? 0);
        var envelope = CreateEnvelope(target.Service.PrimaryActorId, payload, commandId, correlationId);
        await _dispatchPort.DispatchAsync(target.Service.PrimaryActorId, envelope, ct);
        return CreateReceipt(target, target.Service.PrimaryActorId, commandId, correlationId, runId);
    }

    private async Task<ServiceInvocationAcceptedReceipt> DispatchScriptingAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        CancellationToken ct)
    {
        if (_scriptRuntimeCommandPort is not { } scriptRuntimeCommandPort)
        {
            throw new InvalidOperationException(
                "Scripting capability is not enabled on this host; scripting services cannot be invoked.");
        }

        var plan = target.Artifact.DeploymentPlan.ScriptingPlan;
        var commandId = ResolveCommandId(request);
        var correlationId = ResolveCorrelationId(request, commandId);
        var runId = ResolveRunId(request, commandId);
        var registration = await RegisterRunAsync(
            target,
            request,
            runId,
            commandId,
            correlationId,
            target.Service.PrimaryActorId,
            ServiceImplementationKind.Scripting,
            ct);
        await scriptRuntimeCommandPort.RunRuntimeAsync(
            target.Service.PrimaryActorId,
            runId,
            commandId,
            correlationId,
            request.Payload?.Clone(),
            plan.Revision,
            plan.DefinitionActorId,
            request.Payload?.TypeUrl ?? string.Empty,
            request.Identity?.TenantId,
            registration.RunActorId,
            $"service-run-source:{runId}:{commandId}",
            request.ServiceRunCompletionNotificationTarget?.ExpiresAtUnixMs ?? 0,
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
        var callerCredential = BuildWorkflowCallerCredential(chatRequest, request);
        var plan = target.Artifact.DeploymentPlan.WorkflowPlan;
        if (plan.ExecutionMode == ExternalCapabilityExecutionMode.Unspecified ||
            !System.Enum.IsDefined(plan.ExecutionMode))
        {
            throw new InvalidOperationException("Workflow service deployment execution mode is required.");
        }
        var bindingIdentity = WorkflowServiceDeploymentPlanIntegrity.ResolveBindingIdentity(
            target.Artifact,
            target.Service.RevisionId);
        var definitionActorId = ResolveWorkflowServiceDefinitionActorId(target, plan);
        var commandId = ResolveCommandId(request);
        var correlationId = ResolveCorrelationId(request, commandId);
        var definition = new WorkflowDefinitionBinding(
            definitionActorId,
            plan.WorkflowName,
            plan.WorkflowYaml,
            plan.InlineWorkflowYamls,
            plan.ExecutionMode,
            ResolveAuthoritativeScopeId(request, chatRequest),
            string.IsNullOrWhiteSpace(request.RunOrigin)
                ? WorkflowRunOrigins.ServiceInvoke
                : request.RunOrigin.Trim(),
            request.ScheduleId?.Trim() ?? string.Empty,
            SourceKind: "service_revision",
            CapabilityAdmissionPlan: plan.CapabilityAdmissionPlan?.Clone(),
            WorkflowId: bindingIdentity.WorkflowId,
            RevisionId: bindingIdentity.RevisionId);
        var requestedRunId = request.RequestedRunId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedRunId))
        {
            return await DispatchExactWorkflowRunAsync(
                target,
                request,
                definition,
                chatRequest,
                callerCredential,
                requestedRunId,
                commandId,
                correlationId,
                ct);
        }

        var run = await _workflowRunProvisioningPort.CreateRunAsync(definition, ct);
        var serviceRunId = run.ActorId;
        var serviceRunRegistration = await RegisterRunAsync(
            target,
            request,
            serviceRunId,
            commandId,
            correlationId,
            run.ActorId,
            ServiceImplementationKind.Workflow,
            ct);
        var workflowChatRequest = ToWorkflowChatRequest(chatRequest, request, target, run.ActorId, callerCredential);
        var envelope = CreateEnvelope(run.ActorId, Any.Pack(workflowChatRequest), commandId, correlationId);
        var admission = await _dispatchPort.DispatchAsync(run.ActorId, envelope, ct);
        if (!admission.Accepted)
        {
            await MarkRejectedWorkflowRunFailedAsync(serviceRunRegistration).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(request.RequestedRunId))
                await DestroyRejectedWorkflowRunAsync(run.ActorId).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Workflow service dispatch was not accepted for actor '{run.ActorId}'.");
        }

        return CreateReceipt(target, run.ActorId, commandId, correlationId, serviceRunId);
    }

    private async Task<ServiceInvocationAcceptedReceipt> DispatchExactWorkflowRunAsync(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest request,
        WorkflowDefinitionBinding definition,
        ChatRequestEvent chatRequest,
        Aevatar.Workflow.Abstractions.WorkflowCallerCredential callerCredential,
        string requestedRunId,
        string commandId,
        string correlationId,
        CancellationToken ct)
    {
        if (_workflowRunProvisioningPort is not IWorkflowRunIdentityExecutionPort identityExecutionPort)
        {
            throw new InvalidOperationException(
                "Requested Run identity requires IWorkflowRunIdentityExecutionPort support.");
        }

        await _artifactPreflight.ValidateAsync(
            new WorkflowArtifactCompatibilityRequest(
                definition.WorkflowYaml,
                definition.InlineWorkflowYamls,
                definition.CapabilityAdmissionPlan?.Clone(),
                definition.ExpectedExecutionMode,
                definition.WorkflowId,
                definition.RevisionId),
            ct);
        var registration = await RegisterRunAsync(
            target,
            request,
            requestedRunId,
            commandId,
            correlationId,
            requestedRunId,
            ServiceImplementationKind.Workflow,
            ct);
        try
        {
            var workflowChatRequest = ToWorkflowChatRequest(
                chatRequest,
                request,
                target,
                requestedRunId,
                callerCredential);
            var run = await identityExecutionPort.EnsureRunAndDispatchAsync(
                definition,
                requestedRunId,
                workflowChatRequest,
                commandId,
                correlationId,
                ct);
            if (!string.Equals(run.ActorId, requestedRunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Exact workflow Run provisioning returned actor '{run.ActorId}', not '{requestedRunId}'.");
            }

            return CreateReceipt(target, run.ActorId, commandId, correlationId, requestedRunId);
        }
        catch
        {
            await MarkRejectedWorkflowRunFailedAsync(registration).ConfigureAwait(false);
            throw;
        }
    }

    private async Task MarkRejectedWorkflowRunFailedAsync(ServiceRunRegistrationResult registration)
    {
        try
        {
            await _serviceRunRegistrationPort
                .UpdateStatusAsync(
                    registration.RunActorId,
                    registration.RunId,
                    ServiceRunStatus.Failed,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Rejected workflow service run status update failed: serviceRunActorId={ServiceRunActorId} serviceRunId={ServiceRunId}",
                registration.RunActorId,
                registration.RunId);
        }
    }

    private async Task DestroyRejectedWorkflowRunAsync(string workflowRunActorId)
    {
        try
        {
            await _workflowRunProvisioningPort
                .DestroyAsync(workflowRunActorId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Rejected workflow service dispatch cleanup failed: workflowRunActorId={WorkflowRunActorId}",
                workflowRunActorId);
        }
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

    private WorkflowChatRequestEvent ToWorkflowChatRequest(
        ChatRequestEvent source,
        ServiceInvocationRequest invocationRequest,
        ServiceInvocationResolvedTarget target,
        string workflowRunActorId,
        Aevatar.Workflow.Abstractions.WorkflowCallerCredential callerCredential)
    {
        _logger.LogInformation(
            "Workflow service invocation caller credential prepared. scheduleId={ScheduleId} serviceKey={ServiceKey} endpointId={EndpointId} workflowRunActorId={WorkflowRunActorId} hasConnectorAuthorization={HasConnectorAuthorization} hasLlmOwnerToken={HasLlmOwnerToken} callerCredentialSourceKind={CallerCredentialSourceKind} hasCallerBearerToken={HasCallerBearerToken}",
            invocationRequest.ScheduleId ?? string.Empty,
            target.Service.ServiceKey ?? string.Empty,
            invocationRequest.EndpointId ?? string.Empty,
            workflowRunActorId ?? string.Empty,
            !string.IsNullOrWhiteSpace(source.ConnectorHttpAuthorization),
            !string.IsNullOrWhiteSpace(source.LlmControl?.NyxIdAccessToken) ||
            !string.IsNullOrWhiteSpace(source.LlmControl?.NyxIdOrgToken),
            ResolveCallerCredentialSourceKind(callerCredential),
            !string.IsNullOrWhiteSpace(callerCredential.BearerToken));

        var request = new WorkflowChatRequestEvent
        {
            Prompt = source.Prompt ?? string.Empty,
            SessionId = source.SessionId ?? string.Empty,
            TimeoutMs = source.TimeoutMs,
            ScopeId = source.ScopeId ?? string.Empty,
            CallerCredential = callerCredential,
        };
        foreach (var part in source.InputParts)
        {
            var fileRef = ToWorkflowFileRef(part.FileRef);
            request.InputParts.Add(new WorkflowChatInputPartPayload
            {
                Kind = ResolveWorkflowInputPartKind(part, fileRef),
                Text = part.Text ?? string.Empty,
                DataBase64 = part.DataBase64 ?? string.Empty,
                MediaType = part.MediaType ?? string.Empty,
                Uri = part.Uri ?? string.Empty,
                Name = part.Name ?? string.Empty,
                FileRef = fileRef,
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
            RoutePreference = source.LlmControl?.NyxIdRoutePreference ?? string.Empty,
            SenderNyxIdAccessToken = source.LlmControl?.SenderNyxIdAccessToken ?? string.Empty,
        };
        if (source.LlmControl?.HasMaxToolRoundsOverride == true)
            request.LlmControl.MaxToolRoundsOverride = source.LlmControl.MaxToolRoundsOverride;
        ApplyWorkflowCompletionNotificationTarget(
            request,
            invocationRequest.WorkflowCompletionNotificationTarget);
        return request;
    }

    private static Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind ResolveWorkflowInputPartKind(
        ChatContentPart part,
        Aevatar.Workflow.Abstractions.WorkflowFileRef? fileRef)
    {
        if (fileRef != null)
        {
            var mediaType = string.IsNullOrWhiteSpace(part.MediaType)
                ? fileRef.MediaType
                : part.MediaType;
            if (IsMediaType(mediaType, "image/"))
                return Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Image;
            if (IsMediaType(mediaType, "audio/"))
                return Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Audio;
            if (IsMediaType(mediaType, "video/"))
                return Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Video;
            if (part.Kind == ChatContentPartKind.Text)
                return Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.File;
        }

        var kindValue = (int)part.Kind;
        return System.Enum.IsDefined(typeof(Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind), kindValue)
            ? (Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind)kindValue
            : Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Unspecified;
    }

    private static bool IsMediaType(string? mediaType, string prefix) =>
        !string.IsNullOrWhiteSpace(mediaType) &&
        mediaType.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static Aevatar.Workflow.Abstractions.WorkflowFileRef? ToWorkflowFileRef(ChatFileRef? fileRef) =>
        fileRef is null || !HasFileRefIdentity(fileRef)
            ? null
            : new Aevatar.Workflow.Abstractions.WorkflowFileRef
            {
                FileId = fileRef.FileId ?? string.Empty,
                ArtifactId = fileRef.ArtifactId ?? string.Empty,
                SourceKind = ToWorkflowFileSourceKind(fileRef.SourceKind),
                SourceMessageId = fileRef.SourceMessageId ?? string.Empty,
                SourceResourceKey = fileRef.SourceResourceKey ?? string.Empty,
                FileName = fileRef.FileName ?? string.Empty,
                MediaType = fileRef.MediaType ?? string.Empty,
                SizeBytes = fileRef.SizeBytes,
                Sha256 = fileRef.Sha256 ?? string.Empty,
                CreatedAtUnixMs = fileRef.CreatedAtUnixMs,
                ExpiresAtUnixMs = fileRef.ExpiresAtUnixMs,
                OwnerRunId = fileRef.OwnerRunId ?? string.Empty,
                OwnerScopeId = fileRef.OwnerScopeId ?? string.Empty,
            };

    private static Aevatar.Workflow.Abstractions.WorkflowFileSourceKind ToWorkflowFileSourceKind(
        ChatFileSourceKind sourceKind)
    {
        var sourceKindValue = (int)sourceKind;
        return System.Enum.IsDefined(typeof(Aevatar.Workflow.Abstractions.WorkflowFileSourceKind), sourceKindValue)
            ? (Aevatar.Workflow.Abstractions.WorkflowFileSourceKind)sourceKindValue
            : Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.Unspecified;
    }

    private static bool HasFileRefIdentity(ChatFileRef fileRef) =>
        !string.IsNullOrWhiteSpace(fileRef.FileId) ||
        !string.IsNullOrWhiteSpace(fileRef.ArtifactId);

    private static void ApplyWorkflowCompletionNotificationTarget(
        WorkflowChatRequestEvent workflowRequest,
        WorkflowServiceCompletionNotificationTarget? target)
    {
        if (target is null)
            return;

        workflowRequest.CompletionNotificationTarget = new Aevatar.Workflow.Abstractions.WorkflowCompletionNotificationTarget
        {
            ActorId = target.ActorId,
            DeliveryId = target.DeliveryId,
            ExpiresAtUnixMs = target.ExpiresAtUnixMs,
        };
    }

    private static Aevatar.Workflow.Abstractions.WorkflowCallerCredential BuildWorkflowCallerCredential(
        ChatRequestEvent source,
        ServiceInvocationRequest invocationRequest)
    {
        if (source.CallerDurableCredential != null)
        {
            if (string.IsNullOrWhiteSpace(invocationRequest.ScheduleId))
            {
                throw new InvalidOperationException(
                    "caller_durable_credential is accepted only from scheduled dispatch.");
            }

            if (!string.IsNullOrWhiteSpace(source.ConnectorHttpAuthorization) ||
                !string.IsNullOrWhiteSpace(source.LlmControl?.NyxIdAccessToken) ||
                !string.IsNullOrWhiteSpace(source.LlmControl?.NyxIdOrgToken) ||
                !string.IsNullOrWhiteSpace(source.CallerSourceReadableNyxIdBearerToken))
            {
                throw new InvalidOperationException(
                    "caller_durable_credential must not be combined with raw workflow caller credentials.");
            }
            var authority = ToWorkflowCallerNyxIdAuthority(
                source.CallerDurableCredential.ScheduledCallerNyxIdAuthority);
            if (!HasDurableCallerCredential(source.CallerDurableCredential) && authority == null)
            {
                throw new InvalidOperationException(
                    "caller_durable_credential must include a durable secret reference or scheduled NyxID authority.");
            }

            return new Aevatar.Workflow.Abstractions.WorkflowCallerCredential
            {
                DurableCallerCredential = HasDurableCallerCredential(source.CallerDurableCredential)
                    ? source.CallerDurableCredential.Clone()
                    : null,
                NyxIdAuthority = authority,
            };
        }

        if (string.IsNullOrWhiteSpace(invocationRequest.ScheduleId))
        {
            var connectorCredential = BuildWorkflowCallerCredentialFromConnectorAuthorization(
                source.ConnectorHttpAuthorization,
                source.CallerSourceReadableNyxIdBearerToken,
                source.CallerNyxIdCredentialKind);
            if (!string.IsNullOrWhiteSpace(connectorCredential.BearerToken))
                return connectorCredential;
        }

        return BuildWorkflowCallerCredentialFromToken(source.LlmControl?.NyxIdAccessToken);
    }

    private static Aevatar.Workflow.Abstractions.WorkflowCallerCredential BuildWorkflowCallerCredentialFromConnectorAuthorization(
        string? connectorHttpAuthorization,
        string? sourceReadableUserBearerToken,
        AgentToolNyxIdCredentialKindPayload credentialKind)
    {
        var sourceReadable = WorkflowCallerCredentialTokens.ParseOptional(sourceReadableUserBearerToken);
        if (sourceReadable.IsInvalid)
        {
            throw new ArgumentException(
                "Workflow caller source-readable bearer token is invalid.",
                nameof(sourceReadableUserBearerToken));
        }

        if (string.IsNullOrWhiteSpace(connectorHttpAuthorization))
        {
            if (sourceReadable.IsValid ||
                credentialKind != AgentToolNyxIdCredentialKindPayload.Unspecified)
            {
                throw new ArgumentException(
                    "Typed workflow caller credentials require an execution bearer token.",
                    nameof(connectorHttpAuthorization));
            }
            return new Aevatar.Workflow.Abstractions.WorkflowCallerCredential();
        }

        const string bearerPrefix = "Bearer ";
        var authorization = connectorHttpAuthorization.Trim();
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Connector HTTP authorization must use the Bearer scheme.", nameof(connectorHttpAuthorization));

        var credential = BuildWorkflowCallerCredentialFromToken(authorization[bearerPrefix.Length..]);
        switch (credentialKind)
        {
            case AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer:
                if (sourceReadable.IsValid)
                {
                    throw new ArgumentException(
                        "A supplemental source-readable caller credential requires a typed proxy delegation credential.",
                        nameof(sourceReadableUserBearerToken));
                }
                credential.Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer;
                break;
            case AgentToolNyxIdCredentialKindPayload.ProxyDelegation:
                credential.Kind = NyxIdCallerCredentialKind.ProxyDelegation;
                if (sourceReadable.IsValid)
                {
                    credential.SourceReadableUserBearerToken =
                        sourceReadable.NormalizedBearerToken ?? string.Empty;
                }
                break;
            case AgentToolNyxIdCredentialKindPayload.Unspecified:
                if (sourceReadable.IsValid)
                {
                    throw new ArgumentException(
                        "A supplemental source-readable caller credential requires a typed proxy delegation credential.",
                        nameof(sourceReadableUserBearerToken));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(credentialKind));
        }
        return credential;
    }

    private static Aevatar.Workflow.Abstractions.WorkflowCallerCredential BuildWorkflowCallerCredentialFromToken(string? bearerToken)
    {
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(bearerToken);
        if (parsed.IsInvalid)
            throw new ArgumentException("Workflow caller credential bearer token is invalid.", nameof(bearerToken));

        return new Aevatar.Workflow.Abstractions.WorkflowCallerCredential
        {
            BearerToken = parsed.NormalizedBearerToken ?? string.Empty,
        };
    }

    private static bool HasDurableCallerCredential(DurableCallerCredentialRef? reference) =>
        reference != null && !string.IsNullOrWhiteSpace(reference.Ref);

    private static Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority? ToWorkflowCallerNyxIdAuthority(
        ScheduledCallerNyxIdAuthority? source)
    {
        if (source == null)
            return null;

        var platform = source.Platform?.Trim() ?? string.Empty;
        var externalUserId = source.ExternalUserId?.Trim() ?? string.Empty;
        var scope = source.Scope?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(platform) ||
            string.IsNullOrWhiteSpace(externalUserId) ||
            string.IsNullOrWhiteSpace(scope))
        {
            throw new InvalidOperationException(
                "caller_durable_credential NyxID authority is incomplete.");
        }

        return new Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority
        {
            Platform = platform,
            Tenant = source.Tenant?.Trim() ?? string.Empty,
            ExternalUserId = externalUserId,
            Scope = scope,
            BindingId = source.BindingId?.Trim() ?? string.Empty,
        };
    }

    private static string ResolveCallerCredentialSourceKind(
        Aevatar.Workflow.Abstractions.WorkflowCallerCredential credential) =>
        credential.DurableCallerCredential?.SourceKind == DurableCallerCredentialSourceKind.ScheduledDispatch
            ? "scheduled_dispatch"
            : !string.IsNullOrWhiteSpace(credential.BearerToken)
                ? "bearer_token"
                : string.Empty;

    private async Task<ServiceRunRegistrationResult> RegisterRunAsync(
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
        if (request.ServiceRunCompletionNotificationTarget != null)
            record.CompletionNotificationTarget = request.ServiceRunCompletionNotificationTarget.Clone();
        return await _serviceRunRegistrationPort.RegisterAsync(record, ct);
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

    private static void EnsureStaticRunContextIsDispatcherOwned(ServiceInvocationRequest request)
    {
        if (request.Payload?.Is(ChatRequestEvent.Descriptor) == true)
        {
            var chatRequest = request.Payload.Unpack<ChatRequestEvent>();
            if (chatRequest.RunContext != null)
            {
                throw new InvalidOperationException(
                    "Static service invocation run_context is assigned by the dispatcher.");
            }

            return;
        }

        if (request.ServiceRunCompletionNotificationTarget != null)
        {
            throw new InvalidOperationException(
                "Static service terminal notification requires a ChatRequestEvent payload.");
        }
    }

    private static Any AttachStaticRunContext(
        Any payload,
        string serviceRunActorId,
        string runId,
        string commandId,
        string correlationId,
        long completionNotificationExpiresAtUnixMs)
    {
        if (!payload.Is(ChatRequestEvent.Descriptor))
            return payload.Clone();

        var chatRequest = payload.Unpack<ChatRequestEvent>();
        if (string.IsNullOrWhiteSpace(chatRequest.SessionId))
            chatRequest.SessionId = runId;
        chatRequest.RunContext = new RoleChatRunContext
        {
            RunId = runId,
            CommandId = commandId,
            CorrelationId = correlationId,
            CompletionNotificationActorId = serviceRunActorId,
            CompletionNotificationDeliveryId = $"service-run-source:{runId}:{commandId}",
            CompletionNotificationExpiresAtUnixMs = completionNotificationExpiresAtUnixMs,
        };
        return Any.Pack(chatRequest);
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
        string.IsNullOrWhiteSpace(request.RequestedRunId)
            ? commandId
            : request.RequestedRunId.Trim();

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
