using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.AGUI.Contracts;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowRunCallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

public sealed class AevatarInvocationDispatcher
{
    private const string DirectGAgentPublisherId = "aevatar.tools.invoke_gagent";
    private const string DeletedGAgentActorNameAlias = "actor_name";
    private const string WorkflowBackgroundDeliveryBindingDegradedCode = "binding_degraded";
    private const string DefaultMemberEndpointId = "chat";
    private const string ChannelWorkflowDeliveryUnavailableMessage =
        "This channel bot is not provisioned for workflow result delivery, so the workflow was not started. Open /channels, select this registration, and choose Repair workflow replies. This repairs Aevatar's workflow result delivery binding; provider webhook settings usually do not need changes. You can also start the workflow from a surface that can observe its result.";
    private const string WorkflowBackgroundDeliveryReservationFailedMessage =
        "Workflow result delivery could not be prepared, so the workflow was not started. Retry from this chat, or start the workflow from a surface that can observe its result.";
    private static readonly TimeSpan WorkflowBackgroundDeliveryReservationLifetime = TimeSpan.FromDays(30);
    private static readonly string[] ProtectedCallerMetadataKeys =
    [
        LLMRequestMetadataKeys.ScopeId,
        LLMRequestMetadataKeys.OwnerSubject,
        LLMRequestMetadataKeys.ResponseId,
        LLMRequestMetadataKeys.RequestId,
        LLMRequestMetadataKeys.CallId,
        LLMRequestMetadataKeys.NyxIdAccessToken,
        LLMRequestMetadataKeys.NyxIdOrgToken,
        LLMRequestMetadataKeys.SenderNyxIdAccessToken,
        LLMRequestMetadataKeys.SenderBindingId,
        LLMRequestMetadataKeys.SenderNyxUserId,
        LLMRequestMetadataKeys.ModelOverride,
        LLMRequestMetadataKeys.NyxIdRoutePreference,
        LLMRequestMetadataKeys.MaxToolRoundsOverride,
        LLMRequestMetadataKeys.ConnectedServicesContext,
        "scope_id",
    ];

    private static readonly string[] ForbiddenStartWorkflowRootFields =
    [
        "parent_actor_id",
        "parent_run_id",
        "parent_step_id",
        "root_run_id",
        "depth",
        "requested_depth",
        "workflow_runtime_context",
        "workflow_call_context",
    ];

    private static readonly JsonFormatter ProtoJsonFormatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(false)
            .WithTypeRegistry(TypeRegistry.FromFiles(
                AGUIEvent.Descriptor.File,
                AnyReflection.Descriptor,
                StructReflection.Descriptor,
                WrappersReflection.Descriptor)));

    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly IGAgentActorRegistryQueryPort _actorRegistryQueryPort;
    private readonly ITeamEntryMemberResolver _teamEntryMemberResolver;
    private readonly IMemberPublishedServiceResolver _memberPublishedServiceResolver;
    private readonly IStaticGAgentStreamInvocationPort<AGUIEvent> _teamInvocationPort;
    private readonly ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _workflowDispatchService;
    private readonly IServiceInvocationResolutionPort _serviceInvocationResolutionPort;
    private readonly IServiceInvocationDispatcher _serviceInvocationDispatcher;
    private readonly IInvokeAdmissionAuthorizer _admissionAuthorizer;
    private readonly IServiceRunQueryPort _serviceRunQueryPort;
    private readonly IGAgentRunTerminalQueryPort _terminalQueryPort;
    private readonly IWorkflowExecutionQueryApplicationService _workflowQueryService;
    private readonly IWorkflowRunBackgroundDeliveryRegistrationPort? _workflowRunDeliveryRegistrationPort;
    private readonly IScopeWorkflowQueryPort? _scopeWorkflowQueryPort;
    private readonly ILogger<AevatarInvocationDispatcher> _logger;

    public AevatarInvocationDispatcher(
        IActorDispatchPort actorDispatchPort,
        IGAgentActorRegistryQueryPort actorRegistryQueryPort,
        ITeamEntryMemberResolver teamEntryMemberResolver,
        IMemberPublishedServiceResolver memberPublishedServiceResolver,
        IStaticGAgentStreamInvocationPort<AGUIEvent> teamInvocationPort,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> workflowDispatchService,
        IServiceInvocationResolutionPort serviceInvocationResolutionPort,
        IServiceInvocationDispatcher serviceInvocationDispatcher,
        IInvokeAdmissionAuthorizer admissionAuthorizer,
        IServiceRunQueryPort serviceRunQueryPort,
        IGAgentRunTerminalQueryPort terminalQueryPort,
        IWorkflowExecutionQueryApplicationService workflowQueryService,
        IWorkflowRunBackgroundDeliveryRegistrationPort? workflowRunDeliveryRegistrationPort = null,
        ILogger<AevatarInvocationDispatcher>? logger = null,
        IScopeWorkflowQueryPort? scopeWorkflowQueryPort = null)
    {
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _actorRegistryQueryPort = actorRegistryQueryPort ?? throw new ArgumentNullException(nameof(actorRegistryQueryPort));
        _teamEntryMemberResolver = teamEntryMemberResolver ?? throw new ArgumentNullException(nameof(teamEntryMemberResolver));
        _memberPublishedServiceResolver = memberPublishedServiceResolver ?? throw new ArgumentNullException(nameof(memberPublishedServiceResolver));
        _teamInvocationPort = teamInvocationPort ?? throw new ArgumentNullException(nameof(teamInvocationPort));
        _workflowDispatchService = workflowDispatchService ?? throw new ArgumentNullException(nameof(workflowDispatchService));
        _serviceInvocationResolutionPort = serviceInvocationResolutionPort ?? throw new ArgumentNullException(nameof(serviceInvocationResolutionPort));
        _serviceInvocationDispatcher = serviceInvocationDispatcher ?? throw new ArgumentNullException(nameof(serviceInvocationDispatcher));
        _admissionAuthorizer = admissionAuthorizer ?? throw new ArgumentNullException(nameof(admissionAuthorizer));
        _serviceRunQueryPort = serviceRunQueryPort ?? throw new ArgumentNullException(nameof(serviceRunQueryPort));
        _terminalQueryPort = terminalQueryPort ?? throw new ArgumentNullException(nameof(terminalQueryPort));
        _workflowQueryService = workflowQueryService ?? throw new ArgumentNullException(nameof(workflowQueryService));
        _workflowRunDeliveryRegistrationPort = workflowRunDeliveryRegistrationPort;
        _scopeWorkflowQueryPort = scopeWorkflowQueryPort;
        _logger = logger ?? NullLogger<AevatarInvocationDispatcher>.Instance;
    }

    public async Task<string> InvokeGAgentAsync(string argumentsJson, CancellationToken ct = default) =>
        (await InvokeGAgentForChatRunAsync(null, argumentsJson, ct)).ToolExecutionResultJson;

    // Refactor (iter290/cluster001): Old pattern: GAgent dispatch control was encoded only in ResultJson. New principle: GAgent dispatch returns typed run, target, wait, and stream fields for chat-run observation.
    public async Task<ChatRunToolCompletionRequest> InvokeGAgentForChatRunAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var forbiddenAlias = ProtoToolArguments.RejectForbiddenRootField(
            argumentsJson,
            DeletedGAgentActorNameAlias,
            "agent_kind or actor_id");
        if (forbiddenAlias != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(forbiddenAlias), forbiddenAlias);

        var parsed = ProtoToolArguments.Parse<InvokeGAgentToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(parsed.Error), parsed.Error);

        var request = parsed.Value!;
        var payload = request.Payload;
        var error = ProtoToolArguments.RequirePayload(payload, "payload");
        if (error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(error), error);

        var scope = ResolveChannelAwareInvocationScope();
        if (scope.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(scope.Error), scope.Error);

        var target = await ResolveGAgentActorIdAsync(request, scope.Value!, ct);
        if (target.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(target.Error), target.Error);

        var wait = ResolveWait(request.Wait);
        var commandId = ResolveCommandId();
        var chatRequest = BuildChatRequest(payload, commandId, scope.Value!);
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatRequest),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectGAgentPublisherId, target.ActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
        };

        try
        {
            await _actorDispatchPort.DispatchAsync(target.ActorId, envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var dispatchError = Error(
                "dispatch_failed",
                $"GAgent dispatch failed: {ex.Message}");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(dispatchError), dispatchError);
        }

        return ToChatRunRequest(chatRunRequest, new InvocationToolResult
        {
            RunId = commandId,
            Status = wait == InvocationWaitMode.Ack ? "accepted" : "streaming",
            StreamTopic = wait == InvocationWaitMode.Stream
                ? AevatarInvocationStreamTopics.ForActorRun(target.ActorId, commandId)
                : string.Empty,
            ActorId = target.ActorId,
            CommandId = commandId,
            CorrelationId = commandId,
            Wait = wait,
        }, scope.Value!.ScopeId);
    }

    public async Task<string> InvokeTeamAsync(string argumentsJson, CancellationToken ct = default) =>
        (await InvokeTeamForChatRunAsync(null, argumentsJson, ct)).ToolExecutionResultJson;

    public async Task<string> InvokeMemberAsync(string argumentsJson, CancellationToken ct = default) =>
        (await InvokeMemberForChatRunAsync(null, argumentsJson, ct)).ToolExecutionResultJson;

    public async Task<ChatRunToolCompletionRequest> InvokeMemberForChatRunAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var parsed = ProtoToolArguments.Parse<InvokeMemberToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(parsed.Error), parsed.Error);

        var request = parsed.Value!;
        var payload = request.Payload;
        var endpointId = ResolveMemberEndpointId(request.EndpointId);
        var error = ProtoToolArguments.Require(request.MemberId, "member_id", "member_id is required.") ??
                    ProtoToolArguments.RequirePayload(payload, "payload");
        if (error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(error), error);

        var scope = ResolveChannelAwareInvocationScope();
        if (scope.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(scope.Error), scope.Error);

        var wait = ResolveWait(request.Wait);
        try
        {
            var memberResolution = await _memberPublishedServiceResolver.ResolveAsync(
                new MemberPublishedServiceResolveRequest(scope.Value!.ScopeId, request.MemberId.Trim()),
                ct);
            var resolution = new PublishedServiceInvocationTarget(
                memberResolution.ScopeId,
                memberResolution.MemberId,
                memberResolution.PublishedServiceId);
            var invocationRequest = BuildServiceInvocationRequest(resolution, payload, endpointId);
            var target = await _serviceInvocationResolutionPort.ResolveAsync(invocationRequest, ct);
            await _admissionAuthorizer.AuthorizeAsync(
                target.Service.ServiceKey,
                target.Service.DeploymentId,
                target.Artifact,
                target.Endpoint,
                invocationRequest,
                ct);

            return target.Artifact.ImplementationKind switch
            {
                ServiceImplementationKind.Static =>
                    await InvokeStaticServiceToAcceptanceAsync(
                        chatRunRequest,
                        resolution,
                        payload,
                        endpointId,
                        wait,
                        ct),

                ServiceImplementationKind.Workflow =>
                    await InvokeWorkflowServiceToAcceptanceAsync(
                        chatRunRequest,
                        resolution,
                        endpointId,
                        invocationRequest,
                        target,
                        wait,
                        ct),

                _ => UnsupportedPublishedServiceKind(
                    chatRunRequest,
                    resolution.ScopeId,
                    target.Artifact.ImplementationKind,
                    "unsupported_member_service_kind",
                    "aevatar_invoke_member"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var dispatchError = Error(
                "dispatch_failed",
                $"Member invocation failed: {ex.Message}");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(dispatchError), dispatchError);
        }
    }

    // Refactor (iter290/cluster001): Old pattern: team dispatch control was encoded only in ResultJson. New principle: team dispatch returns typed run, service, endpoint, wait, and completion fields for chat-run observation.
    public async Task<ChatRunToolCompletionRequest> InvokeTeamForChatRunAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var parsed = ProtoToolArguments.Parse<InvokeTeamToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(parsed.Error), parsed.Error);

        var request = parsed.Value!;
        var payload = request.Payload;
        var error = ProtoToolArguments.Require(request.TeamId, "team_id", "team_id is required.") ??
                    ProtoToolArguments.Require(request.EndpointId, "endpoint_id", "endpoint_id is required.") ??
                    ProtoToolArguments.RequirePayload(payload, "payload");
        if (error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(error), error);

        var scope = ResolveTeamInvocationScope();
        if (scope.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(scope.Error), scope.Error);

        var wait = ResolveWait(request.Wait);
        try
        {
            var teamResolution = await _teamEntryMemberResolver.ResolveAsync(
                scope.Value!.ScopeId,
                request.TeamId.Trim(),
                request.EndpointId.Trim(),
                ct);
            var resolution = new PublishedServiceInvocationTarget(
                teamResolution.ScopeId,
                teamResolution.EntryMemberId,
                teamResolution.PublishedServiceId);
            var invocationRequest = BuildServiceInvocationRequest(resolution, payload, request.EndpointId);
            var target = await _serviceInvocationResolutionPort.ResolveAsync(invocationRequest, ct);
            await _admissionAuthorizer.AuthorizeAsync(
                target.Service.ServiceKey,
                target.Service.DeploymentId,
                target.Artifact,
                target.Endpoint,
                invocationRequest,
                ct);

            // TODO(aevatar-team-invoke): fully align this with HTTP Team stream by moving the
            // ImplementationKind splitter into an application-layer Team invocation service.
            // Workflow / Static / Scripting should share the same branching service so
            // aevatar_invoke_team and HTTP /teams/{teamId}/invoke/{endpointId}:stream keep one
            // accepted receipt, service-run registration, stream topic, and observe semantics.
            return target.Artifact.ImplementationKind switch
            {
                ServiceImplementationKind.Static =>
                    await InvokeStaticServiceToAcceptanceAsync(
                        chatRunRequest,
                        resolution,
                        payload,
                        request.EndpointId,
                        wait,
                        ct),

                ServiceImplementationKind.Workflow =>
                    await InvokeWorkflowServiceToAcceptanceAsync(
                        chatRunRequest,
                        resolution,
                        request.EndpointId,
                        invocationRequest,
                        target,
                        wait,
                        ct),

                _ => UnsupportedPublishedServiceKind(
                    chatRunRequest,
                    resolution.ScopeId,
                    target.Artifact.ImplementationKind,
                    "unsupported_team_entry_service_kind",
                    "aevatar_invoke_team"),
            };
        }
        catch (TeamEntryMemberResolutionException ex)
        {
            var resolutionError = Error(ex.Code, ex.Message);
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(resolutionError), resolutionError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var dispatchError = Error(
                "dispatch_failed",
                $"Team invocation failed: {ex.Message}");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(dispatchError), dispatchError);
        }
    }

    public async Task<string> StartWorkflowAsync(string argumentsJson, CancellationToken ct = default) =>
        (await StartWorkflowForChatRunAsync(null, argumentsJson, ct)).ToolExecutionResultJson;

    // Refactor (iter290/cluster001): Old pattern: workflow dispatch control was encoded only in ResultJson. New principle: workflow dispatch returns typed actor, run, wait, and completion fields for chat-run observation.
    public async Task<ChatRunToolCompletionRequest> StartWorkflowForChatRunAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var forbiddenField = ProtoToolArguments.RejectForbiddenRootFields(
            argumentsJson,
            ForbiddenStartWorkflowRootFields,
            "trusted workflow runtime context");
        if (forbiddenField != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(forbiddenField), forbiddenField);

        var parsed = ProtoToolArguments.Parse<StartWorkflowToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(parsed.Error), parsed.Error);

        var request = parsed.Value!;
        var wait = ResolveWait(request.Wait);
        var error = ProtoToolArguments.Require(request.WorkflowId, "workflow_id", "workflow_id is required.") ??
                    ProtoToolArguments.RequirePayload(request.Inputs, "inputs");
        if (error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(error), error);

        var workflowYamls = request.WorkflowYamls.Count == 0
            ? null
            : request.WorkflowYamls
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .ToArray();
        var workflowName = request.WorkflowId.Trim();
        var actorId = string.IsNullOrWhiteSpace(request.ActorId) ? null : request.ActorId.Trim();
        if (TryGetManagedWorkflowRuntimeContext(AgentToolRequestContext.Current, out var workflowRuntimeContext))
        {
            var managedScope = ResolveCallerScope(requireOwner: false);
            if (managedScope.Error != null)
                return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(managedScope.Error), managedScope.Error);

            return await StartManagedSubWorkflowForChatRunAsync(
                chatRunRequest,
                request,
                wait,
                managedScope.Value!,
                workflowRuntimeContext,
                workflowName,
                workflowYamls,
                ct);
        }

        var workflowScope = ResolveWorkflowInvocationScope(AgentToolRequestContext.Current);
        if (workflowScope.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(workflowScope.Error), workflowScope.Error);
        var scope = workflowScope.Value!;

        var backgroundDelivery = ResolveWorkflowBackgroundDelivery(AgentToolRequestContext.Current);
        if (backgroundDelivery.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(backgroundDelivery.Error), backgroundDelivery.Error);
        if (backgroundDelivery.ShouldRegister && _workflowRunDeliveryRegistrationPort is null)
        {
            var deliveryUnavailable = ChannelWorkflowDeliveryUnavailableError();
            return ToChatRunRequest(
                chatRunRequest,
                AevatarInvocationJson.Error(deliveryUnavailable),
                deliveryUnavailable);
        }

        var callerCredential = ResolveWorkflowCallerCredential(AgentToolRequestContext.Current);
        if (callerCredential.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(callerCredential.Error), callerCredential.Error);

        var metadata = BuildPayloadHeaders(request.Inputs.Headers);
        var sourceResolution = await ResolveWorkflowStartSourceAsync(
                scope.ScopeId,
                workflowName,
                actorId,
                workflowYamls,
                ct)
            .ConfigureAwait(false);
        if (sourceResolution.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(sourceResolution.Error), sourceResolution.Error);

        var command = new WorkflowChatRunRequest(
            Prompt: request.Inputs.Prompt,
            Source: sourceResolution.Source!,
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            SessionId: ResolveSessionId(),
            InputParts: ToWorkflowInputParts(request.Inputs),
            Metadata: metadata,
            ScopeId: scope.ScopeId,
            LlmControl: ToWorkflowLlmControl(AgentToolRequestContext.Current),
            CallerCredential: callerCredential.Value);

        return await DispatchWorkflowForChatRunAsync(
            chatRunRequest,
            command,
            wait,
            scope.ScopeId,
            backgroundDelivery,
            ct);
    }

    private async ValueTask<WorkflowStartSourceResolution> ResolveWorkflowStartSourceAsync(
        string scopeId,
        string workflowName,
        string? actorId,
        string[]? workflowYamls,
        CancellationToken ct)
    {
        if (workflowYamls is { Length: > 0 })
            return WorkflowStartSourceResolution.Success(
                WorkflowChatSource.InlineYamlBundle(workflowYamls, workflowName, actorId));

        if (!string.IsNullOrWhiteSpace(actorId))
            return WorkflowStartSourceResolution.Success(
                WorkflowChatSource.DefinitionActor(actorId, workflowName));

        var scopeWorkflow = await TryResolveScopeWorkflowAsync(scopeId, workflowName, ct).ConfigureAwait(false);
        if (scopeWorkflow.Error != null)
            return scopeWorkflow;

        if (scopeWorkflow.Workflow is not null)
        {
            var resolvedWorkflowName = string.IsNullOrWhiteSpace(scopeWorkflow.Workflow.WorkflowName)
                ? null
                : scopeWorkflow.Workflow.WorkflowName.Trim();
            return WorkflowStartSourceResolution.Success(
                WorkflowChatSource.DefinitionActor(scopeWorkflow.Workflow.ActorId.Trim(), resolvedWorkflowName));
        }

        return WorkflowStartSourceResolution.Success(WorkflowChatSource.CatalogWorkflow(workflowName));
    }

    private async ValueTask<WorkflowStartSourceResolution> TryResolveScopeWorkflowAsync(
        string scopeId,
        string workflowId,
        CancellationToken ct)
    {
        if (_scopeWorkflowQueryPort is null)
            return WorkflowStartSourceResolution.NotResolved();

        try
        {
            var lookup = await _scopeWorkflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct)
                .ConfigureAwait(false);
            if (lookup.IsRunnable && !string.IsNullOrWhiteSpace(lookup.Workflow!.ActorId))
                return WorkflowStartSourceResolution.Resolved(lookup.Workflow);

            if (lookup.Status == ScopeWorkflowLookupStatus.NotFound)
                return WorkflowStartSourceResolution.Failed(ScopeWorkflowNotFoundError(scopeId, workflowId, lookup));

            return WorkflowStartSourceResolution.Failed(ScopeWorkflowUnavailableError(scopeId, workflowId, lookup));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Scope workflow lookup failed before workflow start: scopeId={ScopeId} workflowId={WorkflowId}",
                scopeId,
                workflowId);
            return WorkflowStartSourceResolution.Failed(ScopeWorkflowLookupFailedError(scopeId, workflowId));
        }
    }

    private async Task<ChatRunToolCompletionRequest> DispatchWorkflowForChatRunAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        WorkflowChatRunRequest command,
        InvocationWaitMode wait,
        string scopeId,
        WorkflowBackgroundDeliveryResolution backgroundDelivery,
        CancellationToken ct)
    {
        WorkflowBackgroundDeliveryReservationContext? deliveryReservation = null;
        string? workflowCorrelationIdSeed = null;
        if (backgroundDelivery.ShouldRegister)
        {
            var workflowCommandIdSeed = ResolveCommandId();
            workflowCorrelationIdSeed = ResolveWorkflowCorrelationId(workflowCommandIdSeed);
            var reservation = await ReserveWorkflowRunBackgroundDeliveryAsync(
                    workflowCommandIdSeed,
                    backgroundDelivery.WorkflowResultDeliveryCredential!,
                    AgentToolRequestContext.Current,
                    ct);
            if (reservation.Error != null)
            {
                return ToChatRunRequest(
                    chatRunRequest,
                    AevatarInvocationJson.Error(reservation.Error),
                    reservation.Error);
            }

            deliveryReservation = reservation.Context;
        }

        command = command with
        {
            CommandIdSeed = deliveryReservation?.Reservation.ExpectedWorkflowCommandId,
            CorrelationIdSeed = workflowCorrelationIdSeed,
            CompletionNotificationTarget = ToWorkflowCompletionNotificationTarget(deliveryReservation),
        };

        CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> result;
        try
        {
            result = await _workflowDispatchService.DispatchAsync(command, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow start dispatch threw before acceptance: scopeId={ScopeId} sourceKind={SourceKind} workflowName={WorkflowName} workflowActorId={WorkflowActorId} commandId={CommandId} deliveryActorId={DeliveryActorId}",
                scopeId,
                command.Source.Kind,
                command.Source.WorkflowName ?? string.Empty,
                command.Source.ActorId ?? string.Empty,
                deliveryReservation?.Reservation.ExpectedWorkflowCommandId ?? command.CommandIdSeed ?? string.Empty,
                deliveryReservation?.Receipt.DeliveryActorId ?? string.Empty);
            await TryAbandonWorkflowRunBackgroundDeliveryAsync(
                    deliveryReservation,
                    $"workflow dispatch threw before acceptance: {ex.GetType().Name}")
                .ConfigureAwait(false);
            throw;
        }
        if (!result.Succeeded || result.Receipt == null)
        {
            _logger.LogWarning(
                "Workflow start dispatch was not accepted: error={Error} scopeId={ScopeId} sourceKind={SourceKind} workflowName={WorkflowName} workflowActorId={WorkflowActorId} commandId={CommandId} deliveryActorId={DeliveryActorId}",
                result.Error,
                scopeId,
                command.Source.Kind,
                command.Source.WorkflowName ?? string.Empty,
                command.Source.ActorId ?? string.Empty,
                deliveryReservation?.Reservation.ExpectedWorkflowCommandId ?? command.CommandIdSeed ?? string.Empty,
                deliveryReservation?.Receipt.DeliveryActorId ?? string.Empty);
            await TryAbandonWorkflowRunBackgroundDeliveryAsync(
                    deliveryReservation,
                    $"workflow dispatch was not accepted: {result.Error}")
                .ConfigureAwait(false);
            var message = result.Error == WorkflowChatRunStartError.WorkflowNotFound
                ? WorkflowChatRunStartErrorGuidance.WorkflowNotFound
                : $"Workflow start failed: {result.Error}";
            var startError = Error(
                result.Error.ToString(),
                message);
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(startError), startError);
        }

        var receipt = result.Receipt;
        if (result.Admission is { Accepted: false })
        {
            _logger.LogWarning(
                "Workflow start dispatch admission was rejected after receipt creation: scopeId={ScopeId} sourceKind={SourceKind} workflowName={WorkflowName} workflowActorId={WorkflowActorId} actorId={ActorId} commandId={CommandId} deliveryActorId={DeliveryActorId}",
                scopeId,
                command.Source.Kind,
                command.Source.WorkflowName ?? string.Empty,
                command.Source.ActorId ?? string.Empty,
                receipt.ActorId,
                receipt.CommandId,
                deliveryReservation?.Receipt.DeliveryActorId ?? string.Empty);
            await TryAbandonWorkflowRunBackgroundDeliveryAsync(
                    deliveryReservation,
                    "workflow dispatch admission was rejected")
                .ConfigureAwait(false);
            var admissionError = Error(
                "dispatch_not_accepted",
                "Workflow start was not accepted by the target actor inbox.");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(admissionError), admissionError);
        }

        if (!MatchesReservedWorkflowCommand(deliveryReservation, receipt.CommandId))
        {
            _logger.LogWarning(
                "Workflow run background delivery binding degraded after acceptance: code={Code} reason=command_identity_mismatch actorId={ActorId} expectedCommandId={ExpectedCommandId} acceptedCommandId={AcceptedCommandId}",
                WorkflowBackgroundDeliveryBindingDegradedCode,
                receipt.ActorId,
                deliveryReservation?.Reservation.ExpectedWorkflowCommandId ?? string.Empty,
                receipt.CommandId);
        }
        var streamTopic = wait == InvocationWaitMode.Stream
            ? AevatarInvocationStreamTopics.ForActorRun(receipt.ActorId, receipt.CommandId)
            : string.Empty;
        WorkflowRunBackgroundDeliveryReceipt? workflowRunDeliveryReceipt = null;
        if (deliveryReservation != null)
        {
            workflowRunDeliveryReceipt = await RegisterWorkflowRunBackgroundDeliveryAsync(
                    receipt,
                    receipt.CommandId,
                    streamTopic,
                    deliveryReservation,
                    ct)
                .ConfigureAwait(false);
        }

        return ToChatRunRequest(chatRunRequest, new InvocationToolResult
        {
            RunId = receipt.CommandId,
            Status = wait == InvocationWaitMode.Ack ? "accepted" : "streaming",
            StreamTopic = streamTopic,
            ActorId = receipt.ActorId,
            CommandId = receipt.CommandId,
            CorrelationId = receipt.CorrelationId,
            Wait = wait,
            WorkflowRunDelivery = workflowRunDeliveryReceipt,
        }, scopeId);
    }

    private async Task<ChatRunToolCompletionRequest> StartManagedSubWorkflowForChatRunAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        StartWorkflowToolRequest request,
        InvocationWaitMode wait,
        InvocationCallerScope scope,
        AgentWorkflowRuntimeContext workflowRuntimeContext,
        string workflowName,
        IReadOnlyList<string>? workflowYamls,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.ActorId))
        {
            var actorIdError = Error(
                "invalid_arguments",
                "actor_id is not accepted when a workflow runtime context manages child workflow start.",
                "actor_id");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(actorIdError), actorIdError);
        }

        var parentActorId = workflowRuntimeContext.ParentActorId!.Trim();
        var parentRunId = workflowRuntimeContext.ParentRunId!.Trim();
        var parentStepId = workflowRuntimeContext.ParentStepId!.Trim();
        var commandId = ResolveCommandId();
        var invocationId = $"{parentRunId}:workflow_tool:{parentStepId}:{commandId}";
        var managedStart = new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = invocationId,
            ParentRunId = parentRunId,
            ParentStepId = parentStepId,
            WorkflowName = workflowName,
            Input = request.Inputs.Prompt ?? string.Empty,
            Lifecycle = "transient",
            RequestedByActorId = parentActorId,
            RootRunId = string.IsNullOrWhiteSpace(workflowRuntimeContext.RootRunId)
                ? parentRunId
                : workflowRuntimeContext.RootRunId.Trim(),
            RequestedDepth = Math.Max(0, workflowRuntimeContext.Depth) + 1,
        };
        managedStart.InputFileRefs.Add(request.Inputs.InputParts
            .Select(static part => ToWorkflowEventFileRef(part.FileRef))
            .Where(static fileRef => fileRef != null)
            .Select(static fileRef => fileRef!.Clone()));

        if (workflowYamls is { Count: > 0 })
        {
            var yamlError = Error(
                "invalid_arguments",
                "workflow_yamls are not accepted inside a managed workflow runtime context; mount reusable workflows before starting a child run.",
                "workflow_yamls");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(yamlError), yamlError);
        }

        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(managedStart),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(parentActorId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
        };

        try
        {
            await _actorDispatchPort.DispatchAsync(parentActorId, envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var dispatchError = Error(
                "dispatch_failed",
                $"Workflow child start failed: {ex.Message}");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(dispatchError), dispatchError);
        }

        return ToChatRunRequest(chatRunRequest, new InvocationToolResult
        {
            RunId = invocationId,
            Status = "accepted",
            StreamTopic = wait == InvocationWaitMode.Stream
                ? AevatarInvocationStreamTopics.ForActorRun(parentActorId, invocationId)
                : string.Empty,
            ActorId = parentActorId,
            CommandId = commandId,
            CorrelationId = commandId,
            Wait = wait,
        }, scope.ScopeId);
    }

    public async Task<string> ObserveRunAsync(string argumentsJson, CancellationToken ct = default)
    {
        var parsed = ProtoToolArguments.Parse<ObserveRunToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return AevatarInvocationJson.Error(parsed.Error);

        var request = parsed.Value!;
        return request.TargetCase switch
        {
            ObserveRunToolRequest.TargetOneofCase.ServiceRun =>
                await ObserveServiceRunAsync(request.ServiceRun, ct),
            ObserveRunToolRequest.TargetOneofCase.GagentTerminalCorrelation =>
                await ObserveGAgentTerminalCorrelationAsync(request.GagentTerminalCorrelation, ct),
            ObserveRunToolRequest.TargetOneofCase.GagentTerminalSession =>
                await ObserveGAgentTerminalSessionAsync(request.GagentTerminalSession, ct),
            ObserveRunToolRequest.TargetOneofCase.WorkflowCurrentState =>
                await ObserveWorkflowCurrentStateAsync(request.WorkflowCurrentState, ct),
            _ => AevatarInvocationJson.Error(Error(
                "invalid_arguments",
                "one of service_run, gagent_terminal_correlation, gagent_terminal_session, or workflow_current_state is required.",
                "target")),
        };
    }

    private async Task<ChatRunToolCompletionRequest> InvokeServiceToAcceptanceAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        StaticGAgentStreamInvocationRequest invocation,
        PublishedServiceInvocationTarget resolution,
        string endpointId,
        InvocationWaitMode wait,
        CancellationToken ct)
    {
        var acceptedSource = new TaskCompletionSource<StaticGAgentStreamAcceptedReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var invocationTask = _teamInvocationPort.InvokeAsync(
            invocation,
            static (_, _) => ValueTask.CompletedTask,
            (receipt, _) =>
            {
                acceptedSource.TrySetResult(receipt);
                return ValueTask.CompletedTask;
            },
            ct);

        _ = ObserveDetachedInvocationAsync(invocationTask);

        await Task.WhenAny(acceptedSource.Task, invocationTask).WaitAsync(ct);

        if (acceptedSource.Task.Status == TaskStatus.RanToCompletion)
        {
            var signaledAcceptedReceipt = await acceptedSource.Task.WaitAsync(ct);
            return ToChatRunRequest(chatRunRequest, BuildServiceAcceptedResult(
                resolution,
                endpointId,
                signaledAcceptedReceipt,
                wait), resolution.ScopeId);
        }

        var result = await invocationTask;
        if (acceptedSource.Task.Status == TaskStatus.RanToCompletion)
        {
            var completedAcceptedReceipt = await acceptedSource.Task.WaitAsync(ct);
            return ToChatRunRequest(chatRunRequest, BuildServiceAcceptedResult(
                resolution,
                endpointId,
                completedAcceptedReceipt,
                wait), resolution.ScopeId);
        }

        if (!result.Succeeded || result.Accepted == null)
        {
            var startError = Error(
                result.StartError.ToString(),
                $"Service invocation was not accepted: {result.StartError}");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(startError), startError);
        }

        var resultAcceptedReceipt = result.Accepted;
        return ToChatRunRequest(chatRunRequest, BuildServiceAcceptedResult(
            resolution,
            endpointId,
            resultAcceptedReceipt,
            wait), resolution.ScopeId);
    }

    private async Task<ChatRunToolCompletionRequest> InvokeStaticServiceToAcceptanceAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        PublishedServiceInvocationTarget resolution,
        InvocationPayload payload,
        string endpointId,
        InvocationWaitMode wait,
        CancellationToken ct)
    {
        var invocation = BuildStaticInvocationRequest(resolution, payload, endpointId);
        // Refactor (v1/issue1470-first): service wait=complete must return the dispatch receipt only;
        // terminal completion is observed through the service-run readmodel instead of folding live AGUI frames.
        return await InvokeServiceToAcceptanceAsync(
            chatRunRequest,
            invocation,
            resolution,
            endpointId,
            wait,
            ct);
    }

    private async Task<ChatRunToolCompletionRequest> InvokeWorkflowServiceToAcceptanceAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        PublishedServiceInvocationTarget resolution,
        string endpointId,
        ServiceInvocationRequest invocationRequest,
        ServiceInvocationResolvedTarget target,
        InvocationWaitMode wait,
        CancellationToken ct)
    {
        EnsureWorkflowServiceChatTarget(target, invocationRequest);

        var backgroundDelivery = ResolveWorkflowBackgroundDelivery(AgentToolRequestContext.Current);
        if (backgroundDelivery.Error != null)
        {
            return ToChatRunRequest(
                chatRunRequest,
                AevatarInvocationJson.Error(backgroundDelivery.Error),
                backgroundDelivery.Error);
        }
        if (backgroundDelivery.ShouldRegister && _workflowRunDeliveryRegistrationPort is null)
        {
            var deliveryUnavailable = ChannelWorkflowDeliveryUnavailableError();
            return ToChatRunRequest(
                chatRunRequest,
                AevatarInvocationJson.Error(deliveryUnavailable),
                deliveryUnavailable);
        }

        var callerCredential = ResolveWorkflowCallerCredential(AgentToolRequestContext.Current);
        if (callerCredential.Error != null)
        {
            return ToChatRunRequest(
                chatRunRequest,
                AevatarInvocationJson.Error(callerCredential.Error),
                callerCredential.Error);
        }

        ApplyWorkflowServiceInvocationContext(invocationRequest, callerCredential.Value);

        WorkflowBackgroundDeliveryReservationContext? deliveryReservation = null;
        if (backgroundDelivery.ShouldRegister)
        {
            var commandId = ResolveCommandId();
            var correlationId = ResolveWorkflowCorrelationId(commandId);
            var reservation = await ReserveWorkflowRunBackgroundDeliveryAsync(
                    commandId,
                    backgroundDelivery.WorkflowResultDeliveryCredential!,
                    AgentToolRequestContext.Current,
                    ct);
            if (reservation.Error != null)
            {
                return ToChatRunRequest(
                    chatRunRequest,
                    AevatarInvocationJson.Error(reservation.Error),
                    reservation.Error);
            }

            deliveryReservation = reservation.Context;
            invocationRequest.CommandId = commandId;
            invocationRequest.CorrelationId = correlationId;
            invocationRequest.WorkflowCompletionNotificationTarget =
                ToWorkflowServiceCompletionNotificationTarget(deliveryReservation);
        }

        ServiceInvocationAcceptedReceipt serviceReceipt;
        try
        {
            serviceReceipt = await _serviceInvocationDispatcher.DispatchAsync(target, invocationRequest, ct);
        }
        catch (Exception ex)
        {
            await TryAbandonWorkflowRunBackgroundDeliveryAsync(
                    deliveryReservation,
                    $"workflow service dispatch threw before acceptance: {ex.GetType().Name}")
                .ConfigureAwait(false);
            throw;
        }
        var serviceRunId = ResolveServiceRunId(serviceReceipt);
        var receipt = ToWorkflowAcceptedReceipt(serviceReceipt);

        if (!MatchesReservedWorkflowCommand(deliveryReservation, receipt.CommandId))
        {
            _logger.LogWarning(
                "Workflow service background delivery binding degraded after acceptance: code={Code} reason=command_identity_mismatch actorId={ActorId} expectedCommandId={ExpectedCommandId} acceptedCommandId={AcceptedCommandId}",
                WorkflowBackgroundDeliveryBindingDegradedCode,
                receipt.ActorId,
                deliveryReservation?.Reservation.ExpectedWorkflowCommandId ?? string.Empty,
                receipt.CommandId);
        }

        var streamTopic = wait == InvocationWaitMode.Stream
            ? AevatarInvocationStreamTopics.ForServiceRun(
                resolution.ScopeId,
                resolution.PublishedServiceId,
                serviceRunId)
            : string.Empty;

        WorkflowRunBackgroundDeliveryReceipt? workflowRunDeliveryReceipt = null;
        if (deliveryReservation != null)
        {
            workflowRunDeliveryReceipt = await RegisterWorkflowRunBackgroundDeliveryAsync(
                    receipt,
                    serviceRunId,
                    streamTopic,
                    deliveryReservation,
                    ct)
                .ConfigureAwait(false);
        }

        return ToChatRunRequest(
            chatRunRequest,
            new InvocationToolResult
            {
                RunId = serviceRunId,
                Status = wait == InvocationWaitMode.Ack ? "accepted" : "streaming",
                StreamTopic = streamTopic,
                ActorId = receipt.ActorId,
                CommandId = receipt.CommandId,
                CorrelationId = receipt.CorrelationId,
                ServiceId = resolution.PublishedServiceId,
                EndpointId = endpointId.Trim(),
                Wait = wait,
                WorkflowRunDelivery = workflowRunDeliveryReceipt,
            },
            resolution.ScopeId);
    }

    private static void EnsureWorkflowServiceChatTarget(
        ServiceInvocationResolvedTarget target,
        ServiceInvocationRequest invocationRequest)
    {
        if (target.Artifact.ImplementationKind != ServiceImplementationKind.Workflow)
            throw new InvalidOperationException("Only workflow services support workflow service invocation.");
        if (target.Endpoint.Kind != ServiceEndpointKind.Chat)
            throw new InvalidOperationException("Only chat endpoints support workflow service invocation.");
        if (!string.IsNullOrWhiteSpace(target.Endpoint.RequestTypeUrl) &&
            !string.Equals(
                target.Endpoint.RequestTypeUrl,
                invocationRequest.Payload?.TypeUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Endpoint '{target.Endpoint.EndpointId}' expects payload '{target.Endpoint.RequestTypeUrl}', but got '{invocationRequest.Payload?.TypeUrl}'.");
        }

        var plan = target.Artifact.DeploymentPlan.WorkflowPlan;
        if (string.IsNullOrWhiteSpace(target.Service.PrimaryActorId) &&
            string.IsNullOrWhiteSpace(plan.DefinitionActorId))
            throw new InvalidOperationException("Workflow service does not have a definition actor.");
    }

    private static WorkflowChatRunAcceptedReceipt ToWorkflowAcceptedReceipt(
        ServiceInvocationAcceptedReceipt receipt)
    {
        var commandId = string.IsNullOrWhiteSpace(receipt.CommandId)
            ? receipt.RequestId
            : receipt.CommandId;
        var correlationId = string.IsNullOrWhiteSpace(receipt.CorrelationId)
            ? commandId
            : receipt.CorrelationId;
        return new WorkflowChatRunAcceptedReceipt(
            receipt.TargetActorId ?? string.Empty,
            "workflow",
            commandId ?? string.Empty,
            correlationId ?? string.Empty);
    }

    private static string ResolveServiceRunId(ServiceInvocationAcceptedReceipt receipt) =>
        string.IsNullOrWhiteSpace(receipt.RunId)
            ? receipt.CommandId ?? string.Empty
            : receipt.RunId.Trim();

    private static ChatRunToolCompletionRequest UnsupportedPublishedServiceKind(
        ChatRunToolCompletionRequest? chatRunRequest,
        string scopeId,
        ServiceImplementationKind kind,
        string code,
        string toolName)
    {
        var error = Error(
            code,
            $"{toolName} currently supports Static and Workflow services, but the resolved service is {kind}.");

        return ToChatRunRequest(
            chatRunRequest,
            AevatarInvocationJson.Error(error),
            error) with
        {
            ScopeId = scopeId,
        };
    }

    private InvocationToolResult BuildServiceAcceptedResult(
        PublishedServiceInvocationTarget resolution,
        string endpointId,
        StaticGAgentStreamAcceptedReceipt accepted,
        InvocationWaitMode wait)
    {
        var runId = accepted.ServiceReceipt.CommandId;
        return new InvocationToolResult
        {
            RunId = runId,
            Status = wait == InvocationWaitMode.Stream ? "streaming" : "accepted",
            StreamTopic = wait == InvocationWaitMode.Stream
                ? AevatarInvocationStreamTopics.ForServiceRun(resolution.ScopeId, resolution.PublishedServiceId, runId)
                : string.Empty,
            ActorId = accepted.ServiceReceipt.TargetActorId,
            CommandId = accepted.ServiceReceipt.CommandId,
            CorrelationId = accepted.ServiceReceipt.CorrelationId,
            ServiceId = resolution.PublishedServiceId,
            EndpointId = endpointId.Trim(),
            Wait = wait,
        };
    }

    // Refactor (iter290/cluster001): Old pattern: dispatcher-to-chat-run conversion left control facts inside boundary JSON. New principle: conversion mirrors stable control facts into typed completion fields while preserving boundary JSON.
    private static ChatRunToolCompletionRequest ToChatRunRequest(
        ChatRunToolCompletionRequest? request,
        InvocationToolResult result,
        string scopeId)
    {
        var boundaryJson = AevatarInvocationJson.Serialize(result);
        var waitMode = ToChatRunWaitMode(result.Wait);
        var completionObserved = !string.IsNullOrWhiteSpace(result.ResultJson) &&
                                 IsTerminalDispatchStatus(result.Status);
        return ToBaseChatRunRequest(request) with
        {
            ToolExecutionResultJson = boundaryJson,
            RunId = result.RunId ?? string.Empty,
            Status = result.Status ?? string.Empty,
            StreamTopic = result.StreamTopic ?? string.Empty,
            ActorId = result.ActorId ?? string.Empty,
            ServiceId = result.ServiceId ?? string.Empty,
            EndpointId = result.EndpointId ?? string.Empty,
            ScopeId = scopeId ?? string.Empty,
            WaitMode = waitMode,
            CompletionResultJson = result.ResultJson ?? string.Empty,
            CompletionObserved = completionObserved,
            ErrorCode = result.Error?.Code ?? string.Empty,
        };
    }

    private static ChatRunToolCompletionRequest ToChatRunRequest(
        ChatRunToolCompletionRequest? request,
        string boundaryJson,
        InvocationToolError error) =>
        ToBaseChatRunRequest(request) with
        {
            ToolExecutionResultJson = boundaryJson,
            ErrorCode = error.Code ?? string.Empty,
        };

    private static ChatRunToolCompletionRequest ToBaseChatRunRequest(ChatRunToolCompletionRequest? request) =>
        request ?? new ChatRunToolCompletionRequest(
            ResponseId: string.Empty,
            ModelName: null,
            Messages: [],
            ToolCall: new ToolCall
            {
                Id = string.Empty,
                Name = string.Empty,
                ArgumentsJson = string.Empty,
            },
            ArgumentsJson: string.Empty,
            ToolExecutionResultJson: string.Empty,
            LlmRound: 0);

    private static ChatRunSubRunWaitMode ToChatRunWaitMode(InvocationWaitMode wait) =>
        wait switch
        {
            InvocationWaitMode.Ack => ChatRunSubRunWaitMode.Ack,
            InvocationWaitMode.Complete => ChatRunSubRunWaitMode.Complete,
            InvocationWaitMode.Stream => ChatRunSubRunWaitMode.Stream,
            _ => ChatRunSubRunWaitMode.Unspecified,
        };

    private static bool IsTerminalDispatchStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return !status.Equals("accepted", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("streaming", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("running", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) &&
               !status.Equals("unknown", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ObserveDetachedInvocationAsync(Task invocationTask)
    {
        try
        {
            await invocationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller's token can cancel after the accepted receipt has already been returned.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Detached team invocation failed after accepted receipt was returned.");
        }
    }

    private async Task<WorkflowBackgroundDeliveryReservationResult> ReserveWorkflowRunBackgroundDeliveryAsync(
        string workflowCommandId,
        ChannelWorkflowResultDeliveryCredential workflowResultDeliveryCredential,
        AgentToolExecutionContext? context,
        CancellationToken ct)
    {
        var reservation = BuildWorkflowRunDeliveryReservation(
            workflowCommandId,
            workflowResultDeliveryCredential,
            context);
        if (reservation is null || _workflowRunDeliveryRegistrationPort is null)
        {
            return WorkflowBackgroundDeliveryReservationResult.Failed(Error(
                AgentToolFailureCodes.ChannelWorkflowResultDeliveryUnavailable,
                ChannelWorkflowDeliveryUnavailableMessage));
        }

        try
        {
            var receipt = await _workflowRunDeliveryRegistrationPort
                .ReserveAsync(reservation, ct)
                .ConfigureAwait(false);
            if (receipt is null)
            {
                return WorkflowBackgroundDeliveryReservationResult.Failed(Error(
                    "workflow_background_delivery_reservation_failed",
                    "Workflow background delivery reservation did not return a durable command target."));
            }

            var reservationContext = new WorkflowBackgroundDeliveryReservationContext(reservation, receipt);
            if (!IsDurableWorkflowRunDeliveryReservationReceipt(receipt, reservation))
            {
                await TryAbandonWorkflowRunBackgroundDeliveryAsync(
                        reservationContext,
                        "reservation receipt did not match the requested workflow command")
                    .ConfigureAwait(false);
                return WorkflowBackgroundDeliveryReservationResult.Failed(Error(
                    "workflow_background_delivery_reservation_failed",
                    "Workflow background delivery reservation did not return a durable command target."));
            }

            return WorkflowBackgroundDeliveryReservationResult.Success(
                new WorkflowBackgroundDeliveryReservationContext(
                    reservation,
                    new WorkflowRunBackgroundDeliveryReservationReceipt(
                        receipt.DeliveryActorId.Trim(),
                        receipt.DeliveryId.Trim(),
                        receipt.WorkflowCommandId.Trim())));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Workflow run background delivery reservation failed before dispatch: commandId={CommandId}",
                workflowCommandId);
            return WorkflowBackgroundDeliveryReservationResult.Failed(Error(
                "workflow_background_delivery_reservation_failed",
                WorkflowBackgroundDeliveryReservationFailedMessage));
        }
    }

    private async Task<WorkflowRunBackgroundDeliveryReceipt> RegisterWorkflowRunBackgroundDeliveryAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        string serviceRunId,
        string streamTopic,
        WorkflowBackgroundDeliveryReservationContext deliveryReservation,
        CancellationToken ct)
    {
        var registration = BuildWorkflowRunDeliveryRegistration(
            receipt,
            serviceRunId,
            streamTopic,
            deliveryReservation.Reservation);
        var fallbackReceipt = BuildWorkflowRunDeliveryFallbackReceipt(
            registration,
            deliveryReservation.Receipt);

        if (_workflowRunDeliveryRegistrationPort is null)
        {
            _logger.LogWarning(
                "Workflow run background delivery binding degraded after acceptance: code={Code} reason=registration_port_unavailable actorId={ActorId} commandId={CommandId}",
                WorkflowBackgroundDeliveryBindingDegradedCode,
                receipt.ActorId,
                receipt.CommandId);
            return fallbackReceipt;
        }

        try
        {
            var deliveryReceipt = await _workflowRunDeliveryRegistrationPort
                .RegisterAsync(deliveryReservation.Receipt, registration, ct)
                .ConfigureAwait(false);
            if (!IsDurableWorkflowRunDeliveryReceipt(deliveryReceipt, deliveryReservation))
            {
                _logger.LogWarning(
                    "Workflow run background delivery binding degraded after acceptance: code={Code} reason=invalid_registration_receipt actorId={ActorId} commandId={CommandId}",
                    WorkflowBackgroundDeliveryBindingDegradedCode,
                    receipt.ActorId,
                    receipt.CommandId);
                return fallbackReceipt;
            }

            return deliveryReceipt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow run background delivery binding degraded after acceptance: code={Code} reason=registration_failed actorId={ActorId} commandId={CommandId}",
                WorkflowBackgroundDeliveryBindingDegradedCode,
                receipt.ActorId,
                receipt.CommandId);
            return fallbackReceipt;
        }
    }

    private async Task TryAbandonWorkflowRunBackgroundDeliveryAsync(
        WorkflowBackgroundDeliveryReservationContext? deliveryReservation,
        string reason)
    {
        if (deliveryReservation is null || _workflowRunDeliveryRegistrationPort is null)
            return;

        var abandonmentReason = string.IsNullOrWhiteSpace(reason)
            ? "workflow dispatch did not complete registration"
            : reason;
        try
        {
            await _workflowRunDeliveryRegistrationPort
                .AbandonAsync(
                    deliveryReservation.Receipt,
                    abandonmentReason,
                    CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Workflow run background delivery abandonment request accepted: deliveryActorId={DeliveryActorId} commandId={CommandId} reason={Reason}",
                deliveryReservation.Receipt.DeliveryActorId,
                deliveryReservation.Receipt.WorkflowCommandId,
                abandonmentReason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow run background delivery reservation abandonment failed: deliveryActorId={DeliveryActorId} commandId={CommandId}",
                deliveryReservation.Receipt.DeliveryActorId,
                deliveryReservation.Receipt.WorkflowCommandId);
        }
    }

    private static bool IsDurableWorkflowRunDeliveryReservationReceipt(
        WorkflowRunBackgroundDeliveryReservationReceipt? receipt,
        WorkflowRunBackgroundDeliveryReservation reservation) =>
        receipt is not null &&
        !string.IsNullOrWhiteSpace(receipt.DeliveryActorId) &&
        string.Equals(
            receipt.DeliveryId?.Trim(),
            reservation.DeliveryId,
            StringComparison.Ordinal) &&
        string.Equals(
            receipt.WorkflowCommandId?.Trim(),
            reservation.ExpectedWorkflowCommandId,
            StringComparison.Ordinal);

    private static bool IsDurableWorkflowRunDeliveryReceipt(
        WorkflowRunBackgroundDeliveryReceipt? receipt,
        WorkflowBackgroundDeliveryReservationContext deliveryReservation) =>
        receipt is not null &&
        string.Equals(
            receipt.DeliveryActorId?.Trim(),
            deliveryReservation.Receipt.DeliveryActorId?.Trim(),
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(receipt.WorkflowActorId) &&
        string.Equals(
            receipt.WorkflowCommandId?.Trim(),
            deliveryReservation.Reservation.ExpectedWorkflowCommandId,
            StringComparison.Ordinal);

    private static WorkflowRunBackgroundDeliveryReservation? BuildWorkflowRunDeliveryReservation(
        string workflowCommandId,
        ChannelWorkflowResultDeliveryCredential workflowResultDeliveryCredential,
        AgentToolExecutionContext? context)
    {
        context ??= AgentToolExecutionContext.Empty;
        var platform = Normalize(context.Channel.Platform);
        var replyMessageId = Normalize(context.Channel.MessageId);
        var normalizedCommandId = Normalize(workflowCommandId);
        if (platform is null || replyMessageId is null || normalizedCommandId is null ||
            !IsUsableWorkflowResultDeliveryCredential(workflowResultDeliveryCredential))
            return null;

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAtUnixMs = nowUnixMs + (long)WorkflowBackgroundDeliveryReservationLifetime.TotalMilliseconds;
        var credentialExpiresAtUnixMs = workflowResultDeliveryCredential.SecretReference?.ExpiresAtUnixMs ?? 0;
        if (credentialExpiresAtUnixMs > 0)
            expiresAtUnixMs = Math.Min(expiresAtUnixMs, credentialExpiresAtUnixMs);

        return new WorkflowRunBackgroundDeliveryReservation(
            deliveryId: $"workflow-delivery:{Guid.NewGuid():N}",
            expectedWorkflowCommandId: normalizedCommandId,
            channelPlatform: platform,
            replyMessageId: replyMessageId,
            platformMessageId: Normalize(context.Channel.PlatformMessageId) ?? string.Empty,
            workflowResultDeliveryCredential: workflowResultDeliveryCredential,
            registrationScopeId: Normalize(context.Channel.RegistrationScopeId) ??
                                 Normalize(context.Caller.ScopeId) ??
                                 string.Empty,
            botRegistrationId: Normalize(context.Channel.BotRegistrationId) ?? string.Empty,
            expiresAtUnixMs: expiresAtUnixMs);
    }

    private static WorkflowRunBackgroundDeliveryRegistration BuildWorkflowRunDeliveryRegistration(
        WorkflowChatRunAcceptedReceipt receipt,
        string serviceRunId,
        string streamTopic,
        WorkflowRunBackgroundDeliveryReservation reservation)
    {
        var normalizedWorkflowRunId = string.IsNullOrWhiteSpace(serviceRunId)
            ? receipt.ActorId
            : serviceRunId.Trim();
        return new WorkflowRunBackgroundDeliveryRegistration(
            DeliveryId: reservation.DeliveryId,
            WorkflowActorId: receipt.ActorId,
            WorkflowRunId: normalizedWorkflowRunId,
            WorkflowCommandId: receipt.CommandId,
            WorkflowCorrelationId: receipt.CorrelationId,
            StreamTopic: streamTopic,
            ChannelPlatform: reservation.ChannelPlatform,
            ReplyMessageId: reservation.ReplyMessageId,
            PlatformMessageId: reservation.PlatformMessageId,
            WorkflowResultDeliveryCredential: reservation.WorkflowResultDeliveryCredential.Clone(),
            RegistrationScopeId: reservation.RegistrationScopeId,
            BotRegistrationId: reservation.BotRegistrationId);
    }

    private static WorkflowRunBackgroundDeliveryReceipt BuildWorkflowRunDeliveryFallbackReceipt(
        WorkflowRunBackgroundDeliveryRegistration registration,
        WorkflowRunBackgroundDeliveryReservationReceipt reservationReceipt) =>
        new()
        {
            DeliveryActorId = reservationReceipt.DeliveryActorId,
            WorkflowActorId = registration.WorkflowActorId,
            WorkflowRunId = registration.WorkflowRunId,
            WorkflowCommandId = registration.WorkflowCommandId,
            WorkflowCorrelationId = registration.WorkflowCorrelationId,
            StreamTopic = registration.StreamTopic,
            ChannelPlatform = registration.ChannelPlatform,
            ReplyMessageId = registration.ReplyMessageId,
            PlatformMessageId = registration.PlatformMessageId,
            RegistrationScopeId = registration.RegistrationScopeId,
        };

    private static bool MatchesReservedWorkflowCommand(
        WorkflowBackgroundDeliveryReservationContext? deliveryReservation,
        string workflowCommandId) =>
        deliveryReservation is null || string.Equals(
            deliveryReservation.Reservation.ExpectedWorkflowCommandId,
            workflowCommandId?.Trim(),
            StringComparison.Ordinal);

    private static Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCompletionNotificationTarget?
        ToWorkflowCompletionNotificationTarget(
        WorkflowBackgroundDeliveryReservationContext? deliveryReservation) =>
        deliveryReservation is null
            ? null
            : new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCompletionNotificationTarget(
                deliveryReservation.Receipt.DeliveryActorId,
                deliveryReservation.Receipt.DeliveryId,
                deliveryReservation.Reservation.ExpiresAtUnixMs);

    private static WorkflowServiceCompletionNotificationTarget? ToWorkflowServiceCompletionNotificationTarget(
        WorkflowBackgroundDeliveryReservationContext? deliveryReservation) =>
        deliveryReservation is null
            ? null
            : new WorkflowServiceCompletionNotificationTarget
            {
                ActorId = deliveryReservation.Receipt.DeliveryActorId,
                DeliveryId = deliveryReservation.Receipt.DeliveryId,
                ExpiresAtUnixMs = deliveryReservation.Reservation.ExpiresAtUnixMs,
            };

    private static InvocationToolError ScopeWorkflowNotFoundError(
        string scopeId,
        string workflowId,
        ScopeWorkflowLookupResult lookup) =>
        Error(
            "scope_workflow_not_found",
            $"Current-scope workflow '{workflowId}' was not found in scope '{scopeId}': {lookup.Reason}. List current scope workflows, choose one runnable descriptor, and retry with its exact workflow_id.",
            "workflow_id");

    private static InvocationToolError ScopeWorkflowUnavailableError(
        string scopeId,
        string workflowId,
        ScopeWorkflowLookupResult lookup) =>
        Error(
            "scope_workflow_not_runnable",
            $"Current-scope workflow '{workflowId}' in scope '{scopeId}' is {lookup.Status} and cannot be started yet: {lookup.Reason}. List current scope workflows and retry when the descriptor is runnable.",
            "workflow_id");

    private static InvocationToolError ScopeWorkflowLookupFailedError(string scopeId, string workflowId) =>
        Error(
            "scope_workflow_lookup_failed",
            $"Current-scope workflow '{workflowId}' in scope '{scopeId}' could not be verified. List current scope workflows and retry when the descriptor is available.",
            "workflow_id");

    private static InvocationToolError ChannelWorkflowDeliveryUnavailableError() =>
        Error(
            AgentToolFailureCodes.ChannelWorkflowResultDeliveryUnavailable,
            ChannelWorkflowDeliveryUnavailableMessage);

    private WorkflowBackgroundDeliveryResolution ResolveWorkflowBackgroundDelivery(
        AgentToolExecutionContext? context)
    {
        context ??= AgentToolExecutionContext.Empty;
        if (Normalize(context.Channel.Platform) is null || Normalize(context.Channel.MessageId) is null)
            return WorkflowBackgroundDeliveryResolution.Disabled();

        var credential = context.Channel.WorkflowResultDeliveryCredential;
        if (IsUsableWorkflowResultDeliveryCredential(credential))
            return WorkflowBackgroundDeliveryResolution.Enabled(credential!);

        _logger.LogInformation(
            "Channel workflow background delivery is unavailable: reason=credential_handle_missing platform={Platform} registrationScopeId={RegistrationScopeId}",
            context.Channel.Platform,
            context.Channel.RegistrationScopeId);
        return WorkflowBackgroundDeliveryResolution.Failed(ChannelWorkflowDeliveryUnavailableError());
    }

    private static bool IsUsableWorkflowResultDeliveryCredential(
        ChannelWorkflowResultDeliveryCredential? credential) =>
        !string.IsNullOrWhiteSpace(credential?.SecretReference?.Ref) &&
        !string.IsNullOrWhiteSpace(credential.SubjectId);

    private StaticGAgentStreamInvocationRequest BuildStaticInvocationRequest(
        PublishedServiceInvocationTarget resolution,
        InvocationPayload payload,
        string endpointId)
    {
        // Refactor (issue1495/first-slice): Old pattern: static service dispatch accepted trusted caller/control facts through legacy Headers.
        // New principle: static service Headers carry only filtered payload headers; service identity and caller fields remain the admission boundary.
        var headers = BuildPayloadHeaders(payload.Headers);
        var identity = new ServiceIdentity
        {
            TenantId = resolution.ScopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = resolution.PublishedServiceId,
        };

        var input = new StaticGAgentStreamInvocationInput(
            Prompt: payload.Prompt,
            SessionId: ResolveSessionId(),
            Headers: headers,
            InputParts: ToGAgentInputParts(payload),
            Caller: new ServiceInvocationCaller
            {
                TenantId = resolution.ScopeId,
                AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                ServiceKey = string.Empty,
            },
            ToolContext: AgentToolRequestContext.Current ?? AgentToolExecutionContext.Empty,
            LlmControl: ToLlmControlContext(AgentToolRequestContext.Current));
        return new StaticGAgentStreamInvocationRequest(identity, endpointId.Trim(), input);
    }

    private ServiceInvocationRequest BuildServiceInvocationRequest(
        PublishedServiceInvocationTarget resolution,
        InvocationPayload payload,
        string endpointId)
    {
        var identity = new ServiceIdentity
        {
            TenantId = resolution.ScopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = resolution.PublishedServiceId,
        };
        var chatRequest = new ChatRequestEvent
        {
            Prompt = payload.Prompt,
            SessionId = ResolveSessionId(),
            ScopeId = resolution.ScopeId,
            ToolContext = AgentToolExecutionContextMapper.ToPayload(
                AgentToolRequestContext.Current ?? AgentToolExecutionContext.Empty),
            LlmControl = ToLlmControlPayload(AgentToolRequestContext.Current),
        };
        chatRequest.InputParts.AddRange(ToChatInputParts(payload));
        var headers = BuildPayloadHeaders(payload.Headers);
        AppendMetadata(chatRequest.Metadata, headers);
        AppendMetadata(chatRequest.Headers, headers);

        return new ServiceInvocationRequest
        {
            Identity = identity,
            EndpointId = endpointId.Trim(),
            Payload = Any.Pack(chatRequest),
            Caller = new ServiceInvocationCaller
            {
                TenantId = resolution.ScopeId,
                AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                ServiceKey = string.Empty,
            },
        };
    }

    private static void ApplyWorkflowServiceInvocationContext(
        ServiceInvocationRequest invocationRequest,
        WorkflowRunCallerCredential? callerCredential)
    {
        if (invocationRequest.Payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true)
            return;

        chatRequest.ConnectorHttpAuthorization = ToConnectorHttpAuthorization(callerCredential);
        chatRequest.CallerSourceReadableNyxIdBearerToken =
            callerCredential?.SourceReadableUserBearerToken?.Trim() ?? string.Empty;
        chatRequest.CallerNyxIdCredentialKind = callerCredential?.Kind switch
        {
            NyxIdCallerCredentialKind.SourceReadableUserBearer =>
                AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
            NyxIdCallerCredentialKind.ProxyDelegation =>
                AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
            _ => AgentToolNyxIdCredentialKindPayload.Unspecified,
        };
        if (!string.IsNullOrWhiteSpace(callerCredential?.SourceReadableUserBearerToken) &&
            chatRequest.LlmControl != null)
        {
            chatRequest.LlmControl.SenderNyxIdAccessToken = string.Empty;
        }
        invocationRequest.Payload = Any.Pack(chatRequest);
    }

    private static string ToConnectorHttpAuthorization(WorkflowRunCallerCredential? callerCredential)
    {
        var token = callerCredential?.BearerToken?.Trim();
        return string.IsNullOrWhiteSpace(token)
            ? string.Empty
            : $"Bearer {token}";
    }

    private async Task<ActorTargetResolution> ResolveGAgentActorIdAsync(
        InvokeGAgentToolRequest request,
        InvocationCallerScope scope,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.ActorId))
        {
            var actorId = request.ActorId.Trim();
            var registrySnapshot = await _actorRegistryQueryPort.ListActorsAsync(scope.ScopeId, ct);
            var isRegistered = registrySnapshot.Groups.Any(g =>
                g.ActorIds.Any(candidate => string.Equals(candidate, actorId, StringComparison.Ordinal)));
            if (!isRegistered)
            {
                return ActorTargetResolution.Failed(Error(
                    "actor_not_found",
                    $"actor_id '{actorId}' is not registered in caller scope '{scope.ScopeId}'.",
                    "actor_id"));
            }

            return ActorTargetResolution.Success(actorId);
        }

        if (string.IsNullOrWhiteSpace(request.AgentKind))
        {
            return ActorTargetResolution.Failed(Error(
                "invalid_arguments",
                "actor_id or agent_kind is required.",
                "actor_id"));
        }

        var agentKind = request.AgentKind.Trim();
        var snapshot = await _actorRegistryQueryPort.ListActorsAsync(scope.ScopeId, ct);
        var group = snapshot.Groups.FirstOrDefault(g =>
            string.Equals(g.AgentKind, agentKind, StringComparison.Ordinal));
        if (group == null || group.ActorIds.Count == 0)
        {
            return ActorTargetResolution.Failed(Error(
                "actor_not_found",
                $"No agent_kind '{agentKind}' is registered in caller scope '{scope.ScopeId}'.",
                "agent_kind"));
        }

        if (group.ActorIds.Count > 1)
        {
            return ActorTargetResolution.Failed(Error(
                "agent_kind_ambiguous",
                $"agent_kind '{agentKind}' resolved to {group.ActorIds.Count} actors. Use actor_id.",
                "agent_kind"));
        }

        return ActorTargetResolution.Success(group.ActorIds[0]);
    }

    private async Task<string> ObserveServiceRunAsync(
        ServiceRunObservationTarget target,
        CancellationToken ct)
    {
        var scope = ResolveCallerScope(requireOwner: false);
        if (scope.Error != null)
            return AevatarInvocationJson.Error(scope.Error);

        var error = ProtoToolArguments.Require(
                        target.ServiceId,
                        "service_run.service_id",
                        "service_run.service_id is required.") ??
                    ProtoToolArguments.Require(
                        target.RunId,
                        "service_run.run_id",
                        "service_run.run_id is required.");
        if (error != null)
            return AevatarInvocationJson.Error(error);

        var serviceRun = await _serviceRunQueryPort.GetByRunIdAsync(
            scope.Value!.ScopeId,
            target.ServiceId.Trim(),
            target.RunId.Trim(),
            ct);
        return serviceRun == null
            ? AevatarInvocationJson.Serialize(NotFound(
                target.RunId.Trim(),
                "service_run_not_found",
                "No service run current-state readmodel matched service_run.service_id and service_run.run_id."))
            : AevatarInvocationJson.Serialize(MapServiceRun(serviceRun));
    }

    private async Task<string> ObserveGAgentTerminalCorrelationAsync(
        GAgentTerminalCorrelationObservationTarget target,
        CancellationToken ct)
    {
        var error = ProtoToolArguments.Require(
                        target.ActorId,
                        "gagent_terminal_correlation.actor_id",
                        "gagent_terminal_correlation.actor_id is required.") ??
                    ProtoToolArguments.Require(
                        target.CorrelationId,
                        "gagent_terminal_correlation.correlation_id",
                        "gagent_terminal_correlation.correlation_id is required.");
        if (error != null)
            return AevatarInvocationJson.Error(error);

        var snapshot = await _terminalQueryPort.GetByCorrelationIdAsync(
            target.ActorId.Trim(),
            target.CorrelationId.Trim(),
            ct);
        return snapshot == null
            ? AevatarInvocationJson.Serialize(NotFound(
                target.CorrelationId.Trim(),
                "gagent_terminal_not_found",
                "No GAgent terminal readmodel matched gagent_terminal_correlation.actor_id and gagent_terminal_correlation.correlation_id."))
            : AevatarInvocationJson.Serialize(MapTerminal(snapshot, target.CorrelationId.Trim()));
    }

    private async Task<string> ObserveGAgentTerminalSessionAsync(
        GAgentTerminalSessionObservationTarget target,
        CancellationToken ct)
    {
        var error = ProtoToolArguments.Require(
                        target.ActorId,
                        "gagent_terminal_session.actor_id",
                        "gagent_terminal_session.actor_id is required.") ??
                    ProtoToolArguments.Require(
                        target.SessionId,
                        "gagent_terminal_session.session_id",
                        "gagent_terminal_session.session_id is required.");
        if (error != null)
            return AevatarInvocationJson.Error(error);

        var snapshot = await _terminalQueryPort.GetBySessionIdAsync(
            target.ActorId.Trim(),
            target.SessionId.Trim(),
            ct);
        return snapshot == null
            ? AevatarInvocationJson.Serialize(NotFound(
                target.SessionId.Trim(),
                "gagent_terminal_not_found",
                "No GAgent terminal readmodel matched gagent_terminal_session.actor_id and gagent_terminal_session.session_id."))
            : AevatarInvocationJson.Serialize(MapTerminal(snapshot, target.SessionId.Trim()));
    }

    private async Task<string> ObserveWorkflowCurrentStateAsync(
        WorkflowCurrentStateObservationTarget target,
        CancellationToken ct)
    {
        var error = ProtoToolArguments.Require(
            target.ActorId,
            "workflow_current_state.actor_id",
            "workflow_current_state.actor_id is required.");
        if (error != null)
            return AevatarInvocationJson.Error(error);

        var snapshot = await _workflowQueryService.GetWorkflowActorCurrentStateAsync(target.ActorId.Trim(), ct);
        if (snapshot == null)
        {
            return AevatarInvocationJson.Serialize(NotFound(
                target.CommandId.Trim(),
                "workflow_current_state_not_found",
                "No workflow current-state readmodel matched workflow_current_state.actor_id."));
        }

        if (!string.IsNullOrWhiteSpace(target.CommandId) &&
            !string.Equals(snapshot.LastCommandId, target.CommandId.Trim(), StringComparison.Ordinal))
        {
            return AevatarInvocationJson.Serialize(NotFound(
                target.CommandId.Trim(),
                "workflow_current_state_not_found",
                "Workflow current-state readmodel actor matched, but last_command_id did not match workflow_current_state.command_id."));
        }

        var observedRunId = string.IsNullOrWhiteSpace(target.CommandId)
            ? snapshot.LastCommandId
            : target.CommandId.Trim();
        return AevatarInvocationJson.Serialize(MapWorkflowSnapshot(snapshot, observedRunId));
    }

    private static ObserveRunResult MapTerminal(GAgentRunTerminalSnapshot terminal, string runId) =>
        new()
        {
            RunId = runId,
            Status = terminal.Status.ToString(),
            ActorId = terminal.ActorId,
            CommandId = terminal.CorrelationId,
            StateVersion = terminal.StateVersion,
            RecentEvents =
            {
                new RunEventSummary
                {
                    EventType = terminal.InteractionKind.ToString(),
                    Message = FirstNonEmpty(terminal.ReasonMessage, terminal.ReasonCode, terminal.Status.ToString()) ?? string.Empty,
                    TimestampUtc = terminal.ObservedAt.UtcDateTime.ToString("O"),
                },
            },
        };

    private static ObserveRunResult MapWorkflowSnapshot(
        WorkflowActorSnapshot snapshot,
        string runId)
    {
        var result = new ObserveRunResult
        {
            RunId = runId,
            Status = snapshot.CompletionStatus.ToString(),
            PartialOutput = snapshot.LastOutput,
            ActorId = snapshot.ActorId,
            CommandId = snapshot.LastCommandId,
            StateVersion = snapshot.StateVersion,
        };
        result.RecentEvents.Add(new RunEventSummary
        {
            EventType = "workflow_actor_snapshot",
            Message = string.IsNullOrWhiteSpace(snapshot.LastError) ? snapshot.LastOutput : snapshot.LastError,
            TimestampUtc = snapshot.LastUpdatedAtUtc?.ToDateTime().ToUniversalTime().ToString("O") ?? string.Empty,
        });
        return result;
    }

    private static ObserveRunResult NotFound(
        string runId,
        string code,
        string message) =>
        new()
        {
            RunId = runId,
            Error = Error(code, message),
        };

    private static ObserveRunResult MapServiceRun(ServiceRunSnapshot snapshot) =>
        new()
        {
            RunId = snapshot.RunId,
            Status = snapshot.Status.ToString(),
            ActorId = FirstNonEmpty(snapshot.TargetActorId, snapshot.ActorId) ?? string.Empty,
            CommandId = snapshot.CommandId,
            StateVersion = snapshot.StateVersion,
            RecentEvents =
            {
                new RunEventSummary
                {
                    EventType = "service_run_current_state",
                    Message = snapshot.Status.ToString(),
                    TimestampUtc = snapshot.UpdatedAt.UtcDateTime.ToString("O"),
                },
            },
        };

    private static ChatRequestEvent BuildChatRequest(
        InvocationPayload payload,
        string commandId,
        InvocationCallerScope scope)
    {
        // Refactor (iter1353/cluster-001): Old pattern: stamp trusted caller/control to Headers/Metadata.
        // New principle: typed ScopeId/ToolContext/LlmControl are authority.
        var headers = BuildPayloadHeaders(payload.Headers);
        var request = new ChatRequestEvent
        {
            Prompt = payload.Prompt,
            SessionId = commandId,
            ScopeId = scope.ScopeId,
            ToolContext = AgentToolExecutionContextMapper.ToPayload(
                AgentToolRequestContext.Current ?? AgentToolExecutionContext.Empty),
            LlmControl = ToLlmControlPayload(AgentToolRequestContext.Current),
        };
        AppendMetadata(request.Headers, headers);
        AppendMetadata(request.Metadata, headers);
        request.InputParts.Add(ToChatInputParts(payload));
        return request;
    }

    private static Dictionary<string, string> BuildPayloadHeaders(
        Google.Protobuf.Collections.MapField<string, string>? headers)
    {
        // Refactor (iter1353/cluster-001): Old pattern: stamp trusted caller/control to Headers/Metadata.
        // New principle: typed ScopeId/ToolContext/LlmControl are authority.
        var filteredHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        if (headers == null)
            return filteredHeaders;

        foreach (var (key, value) in headers)
        {
            var normalizedKey = Normalize(key);
            if (normalizedKey != null && !IsProtectedCallerMetadataKey(normalizedKey))
                filteredHeaders[normalizedKey] = value ?? string.Empty;
        }

        return filteredHeaders;
    }

    private static bool IsProtectedCallerMetadataKey(string key) =>
        ProtectedCallerMetadataKeys.Any(protectedKey =>
            string.Equals(protectedKey, key, StringComparison.Ordinal));

    private static void AppendMetadata(
        Google.Protobuf.Collections.MapField<string, string> destination,
        IReadOnlyDictionary<string, string> source)
    {
        foreach (var (key, value) in source)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                destination[key.Trim()] = value.Trim();
        }
    }

    private static LLMControlContextPayload ToLlmControlPayload(AgentToolExecutionContext? context)
    {
        // Refactor (iter1353/cluster-001): Old pattern: stamp trusted caller/control to Headers/Metadata.
        // New principle: typed ScopeId/ToolContext/LlmControl are authority.
        return ToLlmControlContext(context).ToPayload();
    }

    private static LLMControlContext ToLlmControlContext(AgentToolExecutionContext? context)
    {
        // Refactor (iter1353/cluster-001): Old pattern: stamp trusted caller/control to Headers/Metadata.
        // New principle: typed ScopeId/ToolContext/LlmControl are authority.
        context ??= AgentToolExecutionContext.Empty;
        return new LLMControlContext(
            context.Credentials.NyxIdAccessToken,
            context.Credentials.NyxIdOrgToken,
            context.Credentials.SenderNyxIdAccessToken,
            context.Routing.ModelOverride,
            context.Routing.NyxIdRoutePreference,
            context.Routing.MaxToolRoundsOverride,
            context.Routing.UserMemoryPrompt);
    }

    private static WorkflowLlmControl ToWorkflowLlmControl(AgentToolExecutionContext? context)
    {
        context ??= AgentToolExecutionContext.Empty;
        return new WorkflowLlmControl(
            context.Routing.ModelOverride,
            context.Routing.MaxToolRoundsOverride,
            context.Routing.UserMemoryPrompt,
            context.Routing.NyxIdRoutePreference);
    }

    private static WorkflowCallerCredentialResolution ResolveWorkflowCallerCredential(AgentToolExecutionContext? context)
    {
        var credentialKind = context?.Credentials.NyxIdCredentialKind switch
        {
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer =>
                NyxIdCallerCredentialKind.SourceReadableUserBearer,
            AgentToolNyxIdCredentialKind.ProxyDelegation =>
                NyxIdCallerCredentialKind.ProxyDelegation,
            _ => NyxIdCallerCredentialKind.Unspecified,
        };
        if (WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                context?.Credentials.NyxIdAccessToken,
                credentialKind,
                context?.Credentials.SourceReadableNyxIdAccessToken))
        {
            return WorkflowCallerCredentialResolution.Failed(Error(
                WorkflowChatRunStartError.InvalidCallerCredential.ToString(),
                "Caller credential is invalid."));
        }

        var parsed = WorkflowCallerCredentialTokens.ParseOptional(context?.Credentials.NyxIdAccessToken);
        if (parsed.IsMissing)
            return WorkflowCallerCredentialResolution.Success(null);

        var sourceReadableUserBearerToken = credentialKind == NyxIdCallerCredentialKind.ProxyDelegation
            ? AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context?.Credentials)
            : null;
        return WorkflowCallerCredentialResolution.Success(
            new WorkflowRunCallerCredential(
                parsed.NormalizedBearerToken,
                Kind: credentialKind,
                SourceReadableUserBearerToken: sourceReadableUserBearerToken));
    }

    private static bool TryGetManagedWorkflowRuntimeContext(
        AgentToolExecutionContext? context,
        out AgentWorkflowRuntimeContext workflowRuntimeContext)
    {
        workflowRuntimeContext = context?.WorkflowRuntime ?? AgentWorkflowRuntimeContext.Empty;
        return workflowRuntimeContext.HasManagedParent;
    }

    private CallerScopeResolution ResolveCallerScope(bool requireOwner = true) =>
        ResolveCallerScope(ReadInvocationScopeContext(AgentToolRequestContext.Current), requireOwner);

    private static CallerScopeResolution ResolveCallerScope(
        InvocationScopeContext scopeContext,
        bool requireOwner = true)
    {
        if (scopeContext.EffectiveScopeId == null || scopeContext.ResponseId == null ||
            (requireOwner && scopeContext.EffectiveOwnerSubject == null))
        {
            return CallerScopeResolution.Failed(Error(
                "caller_scope_unavailable",
                requireOwner
                    ? "scope_id/owner_scope_id, owner_subject, and response_id/request_id are required in AgentToolRequestContext."
                    : "scope_id/owner_scope_id and response_id/request_id are required in AgentToolRequestContext."));
        }

        return CallerScopeResolution.Success(new InvocationCallerScope(
            scopeContext.EffectiveScopeId,
            scopeContext.EffectiveOwnerSubject ?? string.Empty,
            scopeContext.ResponseId));
    }

    private CallerScopeResolution ResolveChannelAwareInvocationScope() =>
        ResolveCallerScope(ReadInvocationScopeContext(AgentToolRequestContext.Current));

    private CallerScopeResolution ResolveTeamInvocationScope()
    {
        var scopeContext = ReadInvocationScopeContext(AgentToolRequestContext.Current);
        var baseScope = ResolveCallerScope(scopeContext);
        if (baseScope.Error != null)
            return baseScope;

        if (scopeContext.SenderNyxUserId == null)
            return baseScope;

        return CallerScopeResolution.Success(baseScope.Value! with
        {
            ScopeId = scopeContext.SenderNyxUserId,
            OwnerSubject = scopeContext.SenderNyxUserId,
        });
    }

    private static IReadOnlyList<ChatContentPart> ToChatInputParts(InvocationPayload payload) =>
        payload.InputParts.Select(static part => new ChatContentPart
        {
            Kind = part.Kind switch
            {
                InvocationContentPartKind.Text => ChatContentPartKind.Text,
                InvocationContentPartKind.File => ChatContentPartKind.Text,
                InvocationContentPartKind.Image => ChatContentPartKind.Image,
                InvocationContentPartKind.Audio => ChatContentPartKind.Audio,
                InvocationContentPartKind.Video => ChatContentPartKind.Video,
                _ => ChatContentPartKind.Unspecified,
            },
            Text = part.Text,
            DataBase64 = part.DataBase64,
            MediaType = part.MediaType,
            Uri = part.Uri,
            Name = part.Name,
            FileRef = part.FileRef?.Clone(),
        }).ToArray();

    private static IReadOnlyList<GAgentDraftRunInputPart>? ToGAgentInputParts(InvocationPayload payload)
    {
        if (payload.InputParts.Count == 0)
            return null;

        return payload.InputParts.Select(static part => new GAgentDraftRunInputPart
        {
            Kind = part.Kind switch
            {
                InvocationContentPartKind.Text => GAgentDraftRunInputPartKind.Text,
                InvocationContentPartKind.File => GAgentDraftRunInputPartKind.Text,
                InvocationContentPartKind.Image => GAgentDraftRunInputPartKind.Image,
                InvocationContentPartKind.Audio => GAgentDraftRunInputPartKind.Audio,
                InvocationContentPartKind.Video => GAgentDraftRunInputPartKind.Video,
                _ => GAgentDraftRunInputPartKind.Unspecified,
            },
            Text = EmptyToNull(part.Text),
            DataBase64 = EmptyToNull(part.DataBase64),
            MediaType = EmptyToNull(part.MediaType),
            Uri = EmptyToNull(part.Uri),
            Name = EmptyToNull(part.Name),
            FileRef = part.FileRef?.Clone(),
        }).ToArray();
    }

    private static IReadOnlyList<WorkflowChatInputPart>? ToWorkflowInputParts(InvocationPayload payload)
    {
        if (payload.InputParts.Count == 0)
            return null;

        return payload.InputParts.Select(static part => new WorkflowChatInputPart
        {
            Kind = part.Kind switch
            {
                InvocationContentPartKind.Text => Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Text,
                InvocationContentPartKind.File => Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.File,
                InvocationContentPartKind.Image => Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Image,
                InvocationContentPartKind.Audio => Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Audio,
                InvocationContentPartKind.Video => Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Video,
                _ => Aevatar.Workflow.Application.Abstractions.Runs.WorkflowChatInputPartKind.Unspecified,
            },
            Text = EmptyToNull(part.Text),
            DataBase64 = EmptyToNull(part.DataBase64),
            MediaType = EmptyToNull(part.MediaType),
            Uri = EmptyToNull(part.Uri),
            Name = EmptyToNull(part.Name),
            FileRef = ToWorkflowFileRef(part.FileRef),
        }).ToArray();
    }

    private static FileArtifactRef? ToWorkflowFileRef(Aevatar.AI.Abstractions.ChatFileRef? fileRef) =>
        fileRef is null || !HasFileRefIdentity(fileRef)
            ? null
            : new FileArtifactRef
            {
                FileId = EmptyToNull(fileRef.FileId),
                ArtifactId = EmptyToNull(fileRef.ArtifactId),
                SourceKind = fileRef.SourceKind switch
                {
                    Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput => FileArtifactSourceKind.ChatInput,
                    Aevatar.AI.Abstractions.ChatFileSourceKind.FormUpload => FileArtifactSourceKind.FormUpload,
                    Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource => FileArtifactSourceKind.ConnectedServiceResource,
                    Aevatar.AI.Abstractions.ChatFileSourceKind.ExternalResource => FileArtifactSourceKind.ExternalResource,
                    Aevatar.AI.Abstractions.ChatFileSourceKind.Generated => FileArtifactSourceKind.Generated,
                    _ => FileArtifactSourceKind.Unspecified,
                },
                SourceMessageId = EmptyToNull(fileRef.SourceMessageId),
                SourceResourceKey = EmptyToNull(fileRef.SourceResourceKey),
                FileName = EmptyToNull(fileRef.FileName),
                MediaType = EmptyToNull(fileRef.MediaType),
                SizeBytes = fileRef.SizeBytes,
                Sha256 = EmptyToNull(fileRef.Sha256),
                CreatedAtUnixMs = fileRef.CreatedAtUnixMs,
                ExpiresAtUnixMs = fileRef.ExpiresAtUnixMs,
                OwnerRunId = EmptyToNull(fileRef.OwnerRunId),
                OwnerScopeId = EmptyToNull(fileRef.OwnerScopeId),
            };

    private static bool HasFileRefIdentity(Aevatar.AI.Abstractions.ChatFileRef fileRef) =>
        !string.IsNullOrWhiteSpace(fileRef.FileId) ||
        !string.IsNullOrWhiteSpace(fileRef.ArtifactId);

    private static Aevatar.Workflow.Abstractions.WorkflowFileRef? ToWorkflowEventFileRef(
        Aevatar.AI.Abstractions.ChatFileRef? fileRef) =>
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
        Aevatar.AI.Abstractions.ChatFileSourceKind sourceKind)
    {
        var sourceKindValue = (int)sourceKind;
        return System.Enum.IsDefined(typeof(Aevatar.Workflow.Abstractions.WorkflowFileSourceKind), sourceKindValue)
            ? (Aevatar.Workflow.Abstractions.WorkflowFileSourceKind)sourceKindValue
            : Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.Unspecified;
    }

    private static InvocationWaitMode ResolveWait(InvocationWaitMode wait) =>
        wait == InvocationWaitMode.Unspecified
            ? InvocationWaitMode.Stream
            : wait;

    private static string ResolveMemberEndpointId(string endpointId) =>
        Normalize(endpointId) ?? DefaultMemberEndpointId;

    private static string ResolveCommandId() =>
        Normalize(AgentToolRequestContext.CallId)
        ?? Normalize(AgentToolRequestContext.RequestId)
        ?? Guid.NewGuid().ToString("N");

    private static string ResolveWorkflowCorrelationId(string commandId) =>
        new[]
            {
                Normalize(AgentToolRequestContext.RequestId),
                Normalize(AgentToolRequestContext.ResponseId),
            }
            .FirstOrDefault(candidate =>
                candidate is not null &&
                !string.Equals(candidate, commandId, StringComparison.Ordinal))
        ?? $"workflow-correlation:{commandId}";

    private static string ResolveSessionId() =>
        Normalize(AgentToolRequestContext.ResponseId)
        ?? Normalize(AgentToolRequestContext.RequestId)
        ?? Normalize(AgentToolRequestContext.CallId)
        ?? Guid.NewGuid().ToString("N");

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(Normalize).FirstOrDefault(static value => value != null);

    private static InvocationToolError Error(string code, string message, string? field = null) =>
        new()
        {
            Code = string.IsNullOrWhiteSpace(code) ? "invocation_error" : code.Trim().ToLowerInvariant(),
            Message = message,
            Field = field ?? string.Empty,
        };

    private sealed record InvocationCallerScope(string ScopeId, string OwnerSubject, string ResponseId);

    private sealed record InvocationScopeContext(
        string? ScopeId,
        string? OwnerSubject,
        string? ResponseId,
        string? OwnerScopeId,
        string? SenderNyxUserId)
    {
        public string? EffectiveScopeId => OwnerScopeId ?? ScopeId;

        public string? EffectiveOwnerSubject => OwnerScopeId ?? OwnerSubject;
    }

    private static InvocationScopeContext ReadInvocationScopeContext(AgentToolExecutionContext? context)
    {
        context ??= AgentToolExecutionContext.Empty;
        return new InvocationScopeContext(
            Normalize(context.Caller.ScopeId),
            Normalize(context.Caller.OwnerSubject),
            Normalize(context.Caller.ResponseId) ??
            Normalize(context.Request.RequestId) ??
            Normalize(context.Request.CallId),
            Normalize(context.Caller.OwnerScopeId),
            Normalize(context.SenderBinding.NyxUserId));
    }

    private static CallerScopeResolution ResolveWorkflowInvocationScope(AgentToolExecutionContext? context)
    {
        var scopeContext = ReadInvocationScopeContext(context);
        if (scopeContext.EffectiveScopeId is null || scopeContext.ResponseId is null)
        {
            return CallerScopeResolution.Failed(Error(
                "caller_scope_unavailable",
                "scope_id/owner_scope_id and response_id/request_id are required in AgentToolRequestContext."));
        }

        if (scopeContext.EffectiveOwnerSubject is null)
        {
            return CallerScopeResolution.Failed(Error(
                "caller_scope_unavailable",
                "owner_subject is required in AgentToolRequestContext for caller-scope workflow invocation."));
        }

        return CallerScopeResolution.Success(new InvocationCallerScope(
            scopeContext.EffectiveScopeId,
            scopeContext.EffectiveOwnerSubject,
            scopeContext.ResponseId));
    }

    private sealed record PublishedServiceInvocationTarget(
        string ScopeId,
        string MemberId,
        string PublishedServiceId);

    private sealed record WorkflowStartSourceResolution(
        WorkflowChatSource? Source,
        ScopeWorkflowSummary? Workflow,
        InvocationToolError? Error)
    {
        public static WorkflowStartSourceResolution Success(WorkflowChatSource source) => new(source, null, null);

        public static WorkflowStartSourceResolution Resolved(ScopeWorkflowSummary workflow) => new(null, workflow, null);

        public static WorkflowStartSourceResolution NotResolved() => new(null, null, null);

        public static WorkflowStartSourceResolution Failed(InvocationToolError error) => new(null, null, error);
    }

    private sealed record CallerScopeResolution(InvocationCallerScope? Value, InvocationToolError? Error)
    {
        public static CallerScopeResolution Success(InvocationCallerScope scope) => new(scope, null);

        public static CallerScopeResolution Failed(InvocationToolError error) => new(null, error);
    }

    private sealed record WorkflowCallerCredentialResolution(
        WorkflowRunCallerCredential? Value,
        InvocationToolError? Error)
    {
        public static WorkflowCallerCredentialResolution Success(WorkflowRunCallerCredential? credential) =>
            new(credential, null);

        public static WorkflowCallerCredentialResolution Failed(InvocationToolError error) =>
            new(null, error);
    }

    private sealed record WorkflowBackgroundDeliveryResolution(
        bool ShouldRegister,
        ChannelWorkflowResultDeliveryCredential? WorkflowResultDeliveryCredential,
        InvocationToolError? Error)
    {
        public static WorkflowBackgroundDeliveryResolution Disabled() =>
            new(false, null, null);

        public static WorkflowBackgroundDeliveryResolution Enabled(
            ChannelWorkflowResultDeliveryCredential workflowResultDeliveryCredential) =>
            new(true, workflowResultDeliveryCredential, null);

        public static WorkflowBackgroundDeliveryResolution Failed(InvocationToolError error) =>
            new(false, null, error);
    }

    private sealed record WorkflowBackgroundDeliveryReservationContext(
        WorkflowRunBackgroundDeliveryReservation Reservation,
        WorkflowRunBackgroundDeliveryReservationReceipt Receipt);

    private sealed record WorkflowBackgroundDeliveryReservationResult(
        WorkflowBackgroundDeliveryReservationContext? Context,
        InvocationToolError? Error)
    {
        public static WorkflowBackgroundDeliveryReservationResult Success(
            WorkflowBackgroundDeliveryReservationContext context) =>
            new(context, null);

        public static WorkflowBackgroundDeliveryReservationResult Failed(InvocationToolError error) =>
            new(null, error);
    }

    private sealed record ActorTargetResolution(string ActorId, InvocationToolError? Error)
    {
        public static ActorTargetResolution Success(string actorId) => new(actorId, null);

        public static ActorTargetResolution Failed(InvocationToolError error) => new(string.Empty, error);
    }
}
