using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Presentation.AGUI;
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

    public async Task<string> InvokeGAgentAsync(string argumentsJson, CancellationToken ct = default)
    {
        var parsed = ProtoToolArguments.Parse<InvokeGAgentToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return AevatarInvocationJson.Error(parsed.Error);

        var request = parsed.Value!;
        var error = ProtoToolArguments.RequirePayload(request.Payload, "payload") ??
                    ValidateWaitIsDispatchable(ResolveWait(request.Wait), "aevatar_invoke_gagent");
        if (error != null)
            return AevatarInvocationJson.Error(error);

        var scope = ResolveCallerScope();
        if (scope.Error != null)
            return AevatarInvocationJson.Error(scope.Error);

        var target = await ResolveGAgentActorIdAsync(request, scope.Value!, ct);
        if (target.Error != null)
            return AevatarInvocationJson.Error(target.Error);

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
            return AevatarInvocationJson.Error(Error(
                "dispatch_failed",
                $"GAgent dispatch failed: {ex.Message}"));
        }

        return AevatarInvocationJson.Serialize(new InvocationToolResult
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
        });
    }

    public async Task<string> InvokeTeamAsync(string argumentsJson, CancellationToken ct = default)
    {
        var parsed = ProtoToolArguments.Parse<InvokeTeamToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return AevatarInvocationJson.Error(parsed.Error);

        var request = parsed.Value!;
        var error = ProtoToolArguments.Require(request.TeamId, "team_id", "team_id is required.") ??
                    ProtoToolArguments.Require(request.EndpointId, "endpoint_id", "endpoint_id is required.") ??
                    ProtoToolArguments.RequirePayload(request.Payload, "payload");
        if (error != null)
            return AevatarInvocationJson.Error(error);

        var scope = ResolveCallerScope();
        if (scope.Error != null)
            return AevatarInvocationJson.Error(scope.Error);

        var wait = ResolveWait(request.Wait);
        try
        {
            var resolution = await _teamEntryMemberResolver.ResolveAsync(
                scope.Value!.ScopeId,
                request.TeamId.Trim(),
                ct);
            var invocation = BuildStaticInvocationRequest(resolution, request, scope.Value!);
            return wait == InvocationWaitMode.Complete
                ? await InvokeTeamToCompletionAsync(invocation, resolution, request.EndpointId, wait, ct)
                : await InvokeTeamToAcceptanceAsync(invocation, resolution, request.EndpointId, wait, ct);
        }
        catch (TeamEntryMemberResolutionException ex)
        {
            return AevatarInvocationJson.Error(Error(ex.Code, ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AevatarInvocationJson.Error(Error(
                "dispatch_failed",
                $"Team invocation failed: {ex.Message}"));
        }
    }

    public async Task<string> StartWorkflowAsync(string argumentsJson, CancellationToken ct = default)
    {
        var parsed = ProtoToolArguments.Parse<StartWorkflowToolRequest>(argumentsJson);
        if (parsed.Error != null)
            return AevatarInvocationJson.Error(parsed.Error);

        var request = parsed.Value!;
        var wait = ResolveWait(request.Wait);
        var error = ProtoToolArguments.Require(request.WorkflowId, "workflow_id", "workflow_id is required.") ??
                    ProtoToolArguments.RequirePayload(request.Inputs, "inputs") ??
                    ValidateWaitIsDispatchable(wait, "aevatar_start_workflow");
        if (error != null)
            return AevatarInvocationJson.Error(error);

        var scope = ResolveCallerScope();
        if (scope.Error != null)
            return AevatarInvocationJson.Error(scope.Error);

        var metadata = BuildLegacyMetadata(scope.Value!, request.Inputs.Headers);
        metadata[WorkflowRunCommandMetadataKeys.ScopeId] = scope.Value!.ScopeId;
        var command = new WorkflowChatRunRequest(
            Prompt: request.Inputs.Prompt,
            WorkflowName: request.WorkflowId.Trim(),
            ActorId: string.IsNullOrWhiteSpace(request.ActorId) ? null : request.ActorId.Trim(),
            SessionId: ResolveSessionId(),
            InputParts: ToWorkflowInputParts(request.Inputs),
            WorkflowYamls: request.WorkflowYamls.Count == 0
                ? null
                : request.WorkflowYamls
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(static item => item.Trim())
                    .ToArray(),
            Metadata: metadata,
            ScopeId: scope.Value.ScopeId);

        var result = await _workflowDispatchService.DispatchAsync(command, ct);
        if (!result.Succeeded || result.Receipt == null)
        {
            return AevatarInvocationJson.Error(Error(
                result.Error.ToString(),
                $"Workflow start failed: {result.Error}"));
        }

        var receipt = result.Receipt;
        return AevatarInvocationJson.Serialize(new InvocationToolResult
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
        });
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

            var workflow = await _workflowQueryService.GetActorSnapshotAsync(actorId!, ct);
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

    private static InvocationToolError? ValidateWaitIsDispatchable(InvocationWaitMode wait, string toolName) =>
        wait == InvocationWaitMode.Complete
            ? Error(
                "wait_complete_unavailable",
                $"{toolName} supports wait=ack and wait=stream in Unit 1. wait=complete requires a completion observer.",
                "wait")
            : null;

    private async Task<string> InvokeTeamToAcceptanceAsync(
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

        var accepted = await acceptedSource.Task.WaitAsync(ct);
        return AevatarInvocationJson.Serialize(BuildTeamAcceptedResult(
            resolution,
            endpointId,
            accepted,
            wait));
    }

    private async Task<string> InvokeTeamToCompletionAsync(
        StaticGAgentStreamInvocationRequest invocation,
        TeamEntryMemberResolution resolution,
        string endpointId,
        InvocationWaitMode wait,
        CancellationToken ct)
    {
        var frames = new List<AGUIEvent>();
        var result = await _teamInvocationPort.InvokeAsync(
            invocation,
            (frame, _) =>
            {
                frames.Add(frame.Clone());
                return ValueTask.CompletedTask;
            },
            null,
            ct);

        if (!result.Succeeded || result.Accepted == null)
        {
            return AevatarInvocationJson.Error(Error(
                result.StartError.ToString(),
                $"Team invocation was not accepted: {result.StartError}"));
        }

        var accepted = BuildTeamAcceptedResult(resolution, endpointId, result.Accepted, wait);
        accepted.Status = result.CompletionObserved ? result.CompletionStatus.ToString() : "accepted";
        accepted.ResultJson = AevatarInvocationJson.ToJson(new
        {
            completion_status = result.CompletionStatus.ToString(),
            completion_observed = result.CompletionObserved,
            events = frames.Select(static frame => AevatarInvocationToolSchemas.ParseObject(ProtoJsonFormatter.Format(frame))).ToArray(),
        });
        return AevatarInvocationJson.Serialize(accepted);
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
            Status = wait == InvocationWaitMode.Ack ? "accepted" : "streaming",
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
        InvokeTeamToolRequest request,
        InvocationCallerScope scope)
    {
        var headers = BuildLegacyMetadata(scope, request.Payload.Headers);
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
            });
        return new StaticGAgentStreamInvocationRequest(identity, request.EndpointId.Trim(), input);
    }

    private async Task<ActorTargetResolution> ResolveGAgentActorIdAsync(
        InvokeGAgentToolRequest request,
        InvocationCallerScope scope,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.ActorId))
            return ActorTargetResolution.Success(request.ActorId.Trim());

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

        var snapshot = await _workflowQueryService.GetActorSnapshotAsync(query.ActorId.Trim(), ct);
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

        var items = await _workflowQueryService.ListActorTimelineAsync(
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
        var metadata = BuildLegacyMetadata(scope, payload.Headers);
        var request = new ChatRequestEvent
        {
            Prompt = payload.Prompt,
            SessionId = commandId,
            ScopeId = scope.ScopeId,
            ToolContext = ToPayload(AgentToolRequestContext.Current),
        };
        request.Headers[LLMRequestMetadataKeys.RequestId] = commandId;
        AppendMetadata(request.Headers, metadata);
        AppendMetadata(request.Metadata, metadata);
        request.InputParts.Add(ToChatInputParts(payload));
        return request;
    }

    private static Dictionary<string, string> BuildLegacyMetadata(
        InvocationCallerScope scope,
        Google.Protobuf.Collections.MapField<string, string>? headers = null)
    {
        var metadata = AgentToolRequestContext.Current?.ToLegacyMetadata() is { } current
            ? new Dictionary<string, string>(current, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        RemoveProtectedCallerMetadata(metadata);
        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                var normalizedKey = Normalize(key);
                if (normalizedKey != null && !IsProtectedCallerMetadataKey(normalizedKey))
                    metadata[normalizedKey] = value ?? string.Empty;
            }
        }

        StampTrustedCallerMetadata(metadata, scope);
        return metadata;
    }

    private static void RemoveProtectedCallerMetadata(IDictionary<string, string> metadata)
    {
        foreach (var key in ProtectedCallerMetadataKeys)
            metadata.Remove(key);
    }

    private static bool IsProtectedCallerMetadataKey(string key) =>
        ProtectedCallerMetadataKeys.Any(protectedKey =>
            string.Equals(protectedKey, key, StringComparison.Ordinal));

    private static void StampTrustedCallerMetadata(
        IDictionary<string, string> metadata,
        InvocationCallerScope scope)
    {
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.ScopeId, scope.ScopeId);
        SetTrustedMetadata(metadata, "scope_id", scope.ScopeId);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.OwnerSubject, scope.OwnerSubject);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.ResponseId, scope.ResponseId);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.RequestId, AgentToolRequestContext.RequestId);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.CallId, AgentToolRequestContext.CallId);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.NyxIdAccessToken, AgentToolRequestContext.NyxIdAccessToken);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.NyxIdOrgToken, AgentToolRequestContext.NyxIdOrgToken);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.SenderNyxIdAccessToken, AgentToolRequestContext.SenderNyxIdAccessToken);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.SenderBindingId, AgentToolRequestContext.SenderBindingId);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.ModelOverride, AgentToolRequestContext.ModelOverride);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.NyxIdRoutePreference, AgentToolRequestContext.NyxIdRoutePreference);
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.MaxToolRoundsOverride, AgentToolRequestContext.MaxToolRoundsOverride?.ToString());
        SetTrustedMetadata(metadata, LLMRequestMetadataKeys.ConnectedServicesContext, AgentToolRequestContext.ConnectedServicesContext);
    }

    private static void SetTrustedMetadata(
        IDictionary<string, string> metadata,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value.Trim();
    }

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
