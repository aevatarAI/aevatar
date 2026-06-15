using System.Text.Json;
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
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

public sealed class AevatarInvocationDispatcher
{
    private const string DirectGAgentPublisherId = "aevatar.tools.invoke_gagent";
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
        LLMRequestMetadataKeys.ModelOverride,
        LLMRequestMetadataKeys.NyxIdRoutePreference,
        LLMRequestMetadataKeys.MaxToolRoundsOverride,
        LLMRequestMetadataKeys.ConnectedServicesContext,
        "scope_id",
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
    private readonly IStaticGAgentStreamInvocationPort<AGUIEvent> _teamInvocationPort;
    private readonly ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _workflowDispatchService;
    private readonly IServiceRunQueryPort _serviceRunQueryPort;
    private readonly IGAgentRunTerminalQueryPort _terminalQueryPort;
    private readonly IWorkflowExecutionQueryApplicationService _workflowQueryService;
    private readonly ILogger<AevatarInvocationDispatcher> _logger;

    public AevatarInvocationDispatcher(
        IActorDispatchPort actorDispatchPort,
        IGAgentActorRegistryQueryPort actorRegistryQueryPort,
        ITeamEntryMemberResolver teamEntryMemberResolver,
        IStaticGAgentStreamInvocationPort<AGUIEvent> teamInvocationPort,
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> workflowDispatchService,
        IServiceRunQueryPort serviceRunQueryPort,
        IGAgentRunTerminalQueryPort terminalQueryPort,
        IWorkflowExecutionQueryApplicationService workflowQueryService,
        ILogger<AevatarInvocationDispatcher>? logger = null)
    {
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _actorRegistryQueryPort = actorRegistryQueryPort ?? throw new ArgumentNullException(nameof(actorRegistryQueryPort));
        _teamEntryMemberResolver = teamEntryMemberResolver ?? throw new ArgumentNullException(nameof(teamEntryMemberResolver));
        _teamInvocationPort = teamInvocationPort ?? throw new ArgumentNullException(nameof(teamInvocationPort));
        _workflowDispatchService = workflowDispatchService ?? throw new ArgumentNullException(nameof(workflowDispatchService));
        _serviceRunQueryPort = serviceRunQueryPort ?? throw new ArgumentNullException(nameof(serviceRunQueryPort));
        _terminalQueryPort = terminalQueryPort ?? throw new ArgumentNullException(nameof(terminalQueryPort));
        _workflowQueryService = workflowQueryService ?? throw new ArgumentNullException(nameof(workflowQueryService));
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
        var parsed = ProtoToolArguments.Parse<InvokeGAgentToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(parsed.Error), parsed.Error);

        var request = parsed.Value!;
        var error = ProtoToolArguments.RequirePayload(request.Payload, "payload");
        if (error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(error), error);

        var scope = ResolveCallerScope();
        if (scope.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(scope.Error), scope.Error);

        var target = await ResolveGAgentActorIdAsync(request, scope.Value!, ct);
        if (target.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(target.Error), target.Error);

        var wait = ResolveWait(request.Wait);
        var commandId = ResolveCommandId();
        var chatRequest = BuildChatRequest(request.Payload, commandId, scope.Value!);
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
        var error = ProtoToolArguments.Require(request.TeamId, "team_id", "team_id is required.") ??
                    ProtoToolArguments.Require(request.EndpointId, "endpoint_id", "endpoint_id is required.") ??
                    ProtoToolArguments.RequirePayload(request.Payload, "payload");
        if (error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(error), error);

        var scope = ResolveCallerScope();
        if (scope.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(scope.Error), scope.Error);

        var wait = ResolveWait(request.Wait);
        try
        {
            var resolution = await _teamEntryMemberResolver.ResolveAsync(
                scope.Value!.ScopeId,
                request.TeamId.Trim(),
                request.EndpointId.Trim(),
                ct);
            var invocation = BuildStaticInvocationRequest(resolution, request);
            // Refactor (v1/issue1470-first): InvokeTeam wait=complete must return the dispatch receipt only;
            // terminal completion is observed through the service-run readmodel instead of folding live AGUI frames.
            return await InvokeTeamToAcceptanceAsync(chatRunRequest, invocation, resolution, request.EndpointId, wait, ct);
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
        var parsed = ProtoToolArguments.Parse<StartWorkflowToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(parsed.Error), parsed.Error);

        var request = parsed.Value!;
        var wait = ResolveWait(request.Wait);
        var error = ProtoToolArguments.Require(request.WorkflowId, "workflow_id", "workflow_id is required.") ??
                    ProtoToolArguments.RequirePayload(request.Inputs, "inputs");
        if (error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(error), error);

        var scope = ResolveCallerScope();
        if (scope.Error != null)
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(scope.Error), scope.Error);

        // Refactor (iter1353/cluster-001): Old pattern: workflow dispatch stamped trusted caller/control facts into Metadata.
        // New principle: Metadata carries only filtered payload headers; ScopeId, ToolContext, and LlmControl carry trusted facts.
        var metadata = BuildPayloadHeaders(request.Inputs.Headers);
        var workflowYamls = request.WorkflowYamls.Count == 0
            ? null
            : request.WorkflowYamls
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .ToArray();
        var workflowName = request.WorkflowId.Trim();
        var actorId = string.IsNullOrWhiteSpace(request.ActorId) ? null : request.ActorId.Trim();
        var source = workflowYamls is { Length: > 0 }
            ? WorkflowChatSource.InlineYamlBundle(workflowYamls, workflowName, actorId)
            : string.IsNullOrWhiteSpace(actorId)
                ? WorkflowChatSource.CatalogWorkflow(workflowName)
                : WorkflowChatSource.DefinitionActor(actorId, workflowName);
        var command = new WorkflowChatRunRequest(
            Prompt: request.Inputs.Prompt,
            Source: source,
            SessionId: ResolveSessionId(),
            InputParts: ToWorkflowInputParts(request.Inputs),
            Metadata: metadata,
            ScopeId: scope.Value!.ScopeId,
            LlmControl: ToWorkflowLlmControl(AgentToolRequestContext.Current));

        var result = await _workflowDispatchService.DispatchAsync(command, ct);
        if (!result.Succeeded || result.Receipt == null)
        {
            var startError = Error(
                result.Error.ToString(),
                $"Workflow start failed: {result.Error}");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(startError), startError);
        }

        var receipt = result.Receipt;
        return ToChatRunRequest(chatRunRequest, new InvocationToolResult
        {
            RunId = receipt.CommandId,
            Status = wait == InvocationWaitMode.Ack ? "accepted" : "streaming",
            StreamTopic = wait == InvocationWaitMode.Stream
                ? AevatarInvocationStreamTopics.ForActorRun(receipt.ActorId, receipt.CommandId)
                : string.Empty,
            ActorId = receipt.ActorId,
            CommandId = receipt.CommandId,
            CorrelationId = receipt.CorrelationId,
            Wait = wait,
        }, scope.Value!.ScopeId);
    }

    public async Task<string> ObserveRunAsync(string argumentsJson, CancellationToken ct = default)
    {
        var parsed = ProtoToolArguments.Parse<ObserveRunToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return AevatarInvocationJson.Error(parsed.Error);

        var request = parsed.Value!;
        var error = ProtoToolArguments.Require(request.RunId, "run_id", "run_id is required.");
        if (error != null)
            return AevatarInvocationJson.Error(error);

        var runId = request.RunId.Trim();
        var scope = ResolveCallerScope(requireOwner: false);
        if (scope.Error != null)
            return AevatarInvocationJson.Error(scope.Error);

        ServiceRunSnapshot? serviceRun = null;
        if (!string.IsNullOrWhiteSpace(request.ServiceId))
        {
            serviceRun = await _serviceRunQueryPort.GetByRunIdAsync(
                scope.Value!.ScopeId,
                request.ServiceId.Trim(),
                runId,
                ct);
        }

        var actorId = FirstNonEmpty(request.ActorId, serviceRun?.TargetActorId, serviceRun?.ActorId);
        if (!string.IsNullOrWhiteSpace(actorId))
        {
            var terminal = await _terminalQueryPort.GetByCorrelationIdAsync(actorId!, runId, ct)
                           ?? await _terminalQueryPort.GetBySessionIdAsync(actorId!, runId, ct);
            if (terminal != null)
                return AevatarInvocationJson.Serialize(MapTerminal(terminal, runId));

            var workflow = await _workflowQueryService.GetWorkflowActorCurrentStateAsync(actorId!, ct);
            if (workflow != null &&
                (string.IsNullOrWhiteSpace(workflow.LastCommandId) ||
                 string.Equals(workflow.LastCommandId, runId, StringComparison.Ordinal)))
            {
                return AevatarInvocationJson.Serialize(MapWorkflowSnapshot(workflow, runId, request.Take));
            }
        }

        if (serviceRun != null)
            return AevatarInvocationJson.Serialize(MapServiceRun(serviceRun));

        return AevatarInvocationJson.Serialize(new ObserveRunResult
        {
            RunId = runId,
            Error = Error(
                "run_not_found",
                "No registered service run, terminal GAgent snapshot, or workflow snapshot matched the requested run_id."),
        });
    }

    public async Task<string> QueryReadModelAsync(string argumentsJson, CancellationToken ct = default)
    {
        var parsed = ProtoToolArguments.Parse<QueryReadModelToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return AevatarInvocationJson.Error(parsed.Error);

        var request = parsed.Value!;
        var error = ProtoToolArguments.Require(request.ReadmodelName, "readmodel_name", "readmodel_name is required.") ??
                    ProtoToolArguments.RequireMessage(request.Query, "query");
        if (error != null)
            return AevatarInvocationJson.Error(error);

        return request.ReadmodelName.Trim() switch
        {
            AevatarInvocationReadModels.ServiceRunCurrentState => await QueryServiceRunAsync(request.Query, ct),
            AevatarInvocationReadModels.GAgentRunTerminal => await QueryGAgentRunTerminalAsync(request.Query, ct),
            AevatarInvocationReadModels.WorkflowActorCurrentState => await QueryWorkflowActorSnapshotAsync(request.Query, ct),
            AevatarInvocationReadModels.WorkflowActorTimeline => await QueryWorkflowActorTimelineAsync(request.Query, ct),
            _ => AevatarInvocationJson.Serialize(new QueryReadModelResult
            {
                ReadmodelName = request.ReadmodelName,
                Error = Error(
                    "readmodel_not_registered",
                    $"readmodel_name must be one of: {string.Join(", ", AevatarInvocationToolSchemas.ReadModelNames)}",
                    "readmodel_name"),
            }),
        };
    }

    private async Task<ChatRunToolCompletionRequest> InvokeTeamToAcceptanceAsync(
        ChatRunToolCompletionRequest? chatRunRequest,
        StaticGAgentStreamInvocationRequest invocation,
        TeamEntryMemberResolution resolution,
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
            return ToChatRunRequest(chatRunRequest, BuildTeamAcceptedResult(
                resolution,
                endpointId,
                signaledAcceptedReceipt,
                wait), resolution.ScopeId);
        }

        var result = await invocationTask;
        if (acceptedSource.Task.Status == TaskStatus.RanToCompletion)
        {
            var completedAcceptedReceipt = await acceptedSource.Task.WaitAsync(ct);
            return ToChatRunRequest(chatRunRequest, BuildTeamAcceptedResult(
                resolution,
                endpointId,
                completedAcceptedReceipt,
                wait), resolution.ScopeId);
        }

        if (!result.Succeeded || result.Accepted == null)
        {
            var startError = Error(
                result.StartError.ToString(),
                $"Team invocation was not accepted: {result.StartError}");
            return ToChatRunRequest(chatRunRequest, AevatarInvocationJson.Error(startError), startError);
        }

        var resultAcceptedReceipt = result.Accepted;
        return ToChatRunRequest(chatRunRequest, BuildTeamAcceptedResult(
            resolution,
            endpointId,
            resultAcceptedReceipt,
            wait), resolution.ScopeId);
    }

    private InvocationToolResult BuildTeamAcceptedResult(
        TeamEntryMemberResolution resolution,
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

    private StaticGAgentStreamInvocationRequest BuildStaticInvocationRequest(
        TeamEntryMemberResolution resolution,
        InvokeTeamToolRequest request)
    {
        // Refactor (issue1495/first-slice): Old pattern: static team dispatch accepted trusted caller/control facts through legacy Headers.
        // New principle: static team Headers carry only filtered payload headers; service identity and caller fields remain the admission boundary.
        var headers = BuildPayloadHeaders(request.Payload.Headers);
        var identity = new ServiceIdentity
        {
            TenantId = resolution.ScopeId,
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = resolution.PublishedServiceId,
        };

        var input = new StaticGAgentStreamInvocationInput(
            Prompt: request.Payload.Prompt,
            SessionId: ResolveSessionId(),
            Headers: headers,
            InputParts: ToGAgentInputParts(request.Payload),
            Caller: new ServiceInvocationCaller
            {
                TenantId = resolution.ScopeId,
                AppId = ScopeServiceIdentityDefaults.ServiceAppId,
                ServiceKey = string.Empty,
            },
            ToolContext: AgentToolRequestContext.Current ?? AgentToolExecutionContext.Empty,
            LlmControl: ToLlmControlContext(AgentToolRequestContext.Current));
        return new StaticGAgentStreamInvocationRequest(identity, request.EndpointId.Trim(), input);
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

        if (string.IsNullOrWhiteSpace(request.ActorName))
        {
            return ActorTargetResolution.Failed(Error(
                "invalid_arguments",
                "actor_id or actor_name is required.",
                "actor_id"));
        }

        var actorName = request.ActorName.Trim();
        var snapshot = await _actorRegistryQueryPort.ListActorsAsync(scope.ScopeId, ct);
        var group = snapshot.Groups.FirstOrDefault(g =>
            string.Equals(g.GAgentType, actorName, StringComparison.OrdinalIgnoreCase));
        if (group == null || group.ActorIds.Count == 0)
        {
            return ActorTargetResolution.Failed(Error(
                "actor_not_found",
                $"No actor_name '{actorName}' is registered in caller scope '{scope.ScopeId}'.",
                "actor_name"));
        }

        if (group.ActorIds.Count > 1)
        {
            return ActorTargetResolution.Failed(Error(
                "actor_name_ambiguous",
                $"actor_name '{actorName}' resolved to {group.ActorIds.Count} actors. Use actor_id.",
                "actor_name"));
        }

        return ActorTargetResolution.Success(group.ActorIds[0]);
    }

    private async Task<string> QueryServiceRunAsync(ReadModelQuery query, CancellationToken ct)
    {
        var scope = ResolveCallerScope(requireOwner: false);
        if (scope.Error != null)
            return AevatarInvocationJson.Error(scope.Error);

        if (string.IsNullOrWhiteSpace(query.ServiceId))
        {
            return AevatarInvocationJson.Serialize(new QueryReadModelResult
            {
                ReadmodelName = AevatarInvocationReadModels.ServiceRunCurrentState,
                Error = Error("invalid_arguments", "query.service_id is required.", "query.service_id"),
            });
        }

        ServiceRunSnapshot? one = null;
        if (!string.IsNullOrWhiteSpace(query.RunId))
        {
            one = await _serviceRunQueryPort.GetByRunIdAsync(
                scope.Value!.ScopeId,
                query.ServiceId.Trim(),
                query.RunId.Trim(),
                ct);
        }
        else if (!string.IsNullOrWhiteSpace(query.CommandId))
        {
            one = await _serviceRunQueryPort.GetByCommandIdAsync(
                scope.Value!.ScopeId,
                query.ServiceId.Trim(),
                query.CommandId.Trim(),
                ct);
        }

        if (one != null)
        {
            return AevatarInvocationJson.Serialize(new QueryReadModelResult
            {
                ReadmodelName = AevatarInvocationReadModels.ServiceRunCurrentState,
                ResultJson = AevatarInvocationJson.ToJson(one),
                Count = 1,
            });
        }

        var items = await _serviceRunQueryPort.ListAsync(
            new ServiceRunQuery(
                scope.Value!.ScopeId,
                query.ServiceId.Trim(),
                BoundTake(query.Take)),
            ct);
        return AevatarInvocationJson.Serialize(new QueryReadModelResult
        {
            ReadmodelName = AevatarInvocationReadModels.ServiceRunCurrentState,
            ResultJson = AevatarInvocationJson.ToJson(items),
            Count = items.Count,
        });
    }

    private async Task<string> QueryGAgentRunTerminalAsync(ReadModelQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.ActorId))
        {
            return AevatarInvocationJson.Serialize(new QueryReadModelResult
            {
                ReadmodelName = AevatarInvocationReadModels.GAgentRunTerminal,
                Error = Error("invalid_arguments", "query.actor_id is required.", "query.actor_id"),
            });
        }

        var correlationId = FirstNonEmpty(query.CommandId, query.RunId, query.Id);
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return AevatarInvocationJson.Serialize(new QueryReadModelResult
            {
                ReadmodelName = AevatarInvocationReadModels.GAgentRunTerminal,
                Error = Error("invalid_arguments", "query.command_id, query.run_id, or query.id is required.", "query.run_id"),
            });
        }

        var snapshot = await _terminalQueryPort.GetByCorrelationIdAsync(
            query.ActorId.Trim(),
            correlationId!,
            ct);
        return AevatarInvocationJson.Serialize(new QueryReadModelResult
        {
            ReadmodelName = AevatarInvocationReadModels.GAgentRunTerminal,
            ResultJson = snapshot == null ? "null" : AevatarInvocationJson.ToJson(snapshot),
            Count = snapshot == null ? 0 : 1,
        });
    }

    private async Task<string> QueryWorkflowActorSnapshotAsync(ReadModelQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.ActorId))
        {
            return AevatarInvocationJson.Serialize(new QueryReadModelResult
            {
                ReadmodelName = AevatarInvocationReadModels.WorkflowActorCurrentState,
                Error = Error("invalid_arguments", "query.actor_id is required.", "query.actor_id"),
            });
        }

        var snapshot = await _workflowQueryService.GetWorkflowActorCurrentStateAsync(query.ActorId.Trim(), ct);
        return AevatarInvocationJson.Serialize(new QueryReadModelResult
        {
            ReadmodelName = AevatarInvocationReadModels.WorkflowActorCurrentState,
            ResultJson = snapshot == null ? "null" : ProtoJsonFormatter.Format(snapshot),
            Count = snapshot == null ? 0 : 1,
        });
    }

    private async Task<string> QueryWorkflowActorTimelineAsync(ReadModelQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.ActorId))
        {
            return AevatarInvocationJson.Serialize(new QueryReadModelResult
            {
                ReadmodelName = AevatarInvocationReadModels.WorkflowActorTimeline,
                Error = Error("invalid_arguments", "query.actor_id is required.", "query.actor_id"),
            });
        }

        var items = await _workflowQueryService.ListWorkflowRunTimelineExportAsync(
            query.ActorId.Trim(),
            BoundTake(query.Take),
            ct);
        return AevatarInvocationJson.Serialize(new QueryReadModelResult
        {
            ReadmodelName = AevatarInvocationReadModels.WorkflowActorTimeline,
            ResultJson = ProtoJsonFormatter.Format(new ListValue
            {
                Values =
                {
                    items.Select(item => Value.ForStruct(JsonParser.Default.Parse<Struct>(
                        ProtoJsonFormatter.Format(item)))),
                },
            }),
            Count = items.Count,
        });
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
        string runId,
        int take)
    {
        var result = new ObserveRunResult
        {
            RunId = runId,
            Status = snapshot.CompletionStatusValue.ToString(),
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
            ToolContext = ToPayload(AgentToolRequestContext.Current),
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

    private static AgentToolExecutionContextPayload ToPayload(AgentToolExecutionContext? context)
    {
        // Refactor (iter1353/cluster-001): Old pattern: stamp trusted caller/control to Headers/Metadata.
        // New principle: typed ScopeId/ToolContext/LlmControl are authority.
        context ??= AgentToolExecutionContext.Empty;
        var payload = new AgentToolExecutionContextPayload
        {
            Request = new AgentToolRequestIdentityPayload
            {
                RequestId = context.Request.RequestId ?? string.Empty,
                CallId = context.Request.CallId ?? string.Empty,
            },
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = context.Credentials.NyxIdAccessToken ?? string.Empty,
                NyxIdOrgToken = context.Credentials.NyxIdOrgToken ?? string.Empty,
                SenderNyxIdAccessToken = context.Credentials.SenderNyxIdAccessToken ?? string.Empty,
            },
            Caller = new AgentToolCallerContextPayload
            {
                ScopeId = context.Caller.ScopeId ?? string.Empty,
                OwnerSubject = context.Caller.OwnerSubject ?? string.Empty,
                ResponseId = context.Caller.ResponseId ?? string.Empty,
            },
            Channel = new AgentToolChannelContextPayload
            {
                Platform = context.Channel.Platform ?? string.Empty,
                SenderId = context.Channel.SenderId ?? string.Empty,
                RegistrationScopeId = context.Channel.RegistrationScopeId ?? string.Empty,
                MessageId = context.Channel.MessageId ?? string.Empty,
                PlatformMessageId = context.Channel.PlatformMessageId ?? string.Empty,
            },
            SenderBinding = new AgentToolSenderBindingContextPayload
            {
                BindingId = context.SenderBinding.BindingId ?? string.Empty,
            },
            Routing = new LLMRequestRoutingContextPayload
            {
                ModelOverride = context.Routing.ModelOverride ?? string.Empty,
                NyxIdRoutePreference = context.Routing.NyxIdRoutePreference ?? string.Empty,
                UserMemoryPrompt = context.Routing.UserMemoryPrompt ?? string.Empty,
            },
            ConnectedServices = new AgentToolConnectedServicesContextPayload
            {
                ContextJson = context.ConnectedServices.ContextJson ?? string.Empty,
            },
        };
        if (context.Routing.MaxToolRoundsOverride.HasValue)
            payload.Routing.MaxToolRoundsOverride = context.Routing.MaxToolRoundsOverride.Value;
        foreach (var (key, value) in context.ExternalMetadata)
            payload.ExternalMetadata[key] = value;
        return payload;
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
            context.Routing.UserMemoryPrompt);
    }

    private CallerScopeResolution ResolveCallerScope(bool requireOwner = true)
    {
        var scopeId = Normalize(AgentToolRequestContext.ScopeId);
        var ownerSubject = Normalize(AgentToolRequestContext.OwnerSubject);
        var responseId = Normalize(AgentToolRequestContext.ResponseId)
                         ?? Normalize(AgentToolRequestContext.RequestId)
                         ?? Normalize(AgentToolRequestContext.CallId);
        if (scopeId == null || responseId == null || (requireOwner && ownerSubject == null))
        {
            return CallerScopeResolution.Failed(Error(
                "caller_scope_unavailable",
                requireOwner
                    ? "scope_id, owner_subject, and response_id/request_id are required in AgentToolRequestContext."
                    : "scope_id and response_id/request_id are required in AgentToolRequestContext."));
        }

        return CallerScopeResolution.Success(new InvocationCallerScope(
            scopeId,
            ownerSubject ?? string.Empty,
            responseId));
    }

    private static IReadOnlyList<ChatContentPart> ToChatInputParts(InvocationPayload payload) =>
        payload.InputParts.Select(static part => new ChatContentPart
        {
            Kind = part.Kind switch
            {
                InvocationContentPartKind.Text => ChatContentPartKind.Text,
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
                InvocationContentPartKind.Text => WorkflowChatInputPartKind.Text,
                InvocationContentPartKind.Image => WorkflowChatInputPartKind.Image,
                InvocationContentPartKind.Audio => WorkflowChatInputPartKind.Audio,
                InvocationContentPartKind.Video => WorkflowChatInputPartKind.Video,
                _ => WorkflowChatInputPartKind.Unspecified,
            },
            Text = EmptyToNull(part.Text),
            DataBase64 = EmptyToNull(part.DataBase64),
            MediaType = EmptyToNull(part.MediaType),
            Uri = EmptyToNull(part.Uri),
            Name = EmptyToNull(part.Name),
        }).ToArray();
    }

    private static InvocationWaitMode ResolveWait(InvocationWaitMode wait) =>
        wait == InvocationWaitMode.Unspecified
            ? InvocationWaitMode.Stream
            : wait;

    private static int BoundTake(int take) => take <= 0 ? 50 : Math.Clamp(take, 1, 200);

    private static string ResolveCommandId() =>
        Normalize(AgentToolRequestContext.CallId)
        ?? Normalize(AgentToolRequestContext.RequestId)
        ?? Guid.NewGuid().ToString("N");

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

    private sealed record CallerScopeResolution(InvocationCallerScope? Value, InvocationToolError? Error)
    {
        public static CallerScopeResolution Success(InvocationCallerScope scope) => new(scope, null);

        public static CallerScopeResolution Failed(InvocationToolError error) => new(null, error);
    }

    private sealed record ActorTargetResolution(string ActorId, InvocationToolError? Error)
    {
        public static ActorTargetResolution Success(string actorId) => new(actorId, null);

        public static ActorTargetResolution Failed(InvocationToolError error) => new(string.Empty, error);
    }
}
