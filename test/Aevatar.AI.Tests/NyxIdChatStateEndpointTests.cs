using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AGUI.Contracts;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatStateEndpointTests
{
    private const string StateRoute =
        "/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}/state";

    [Fact]
    public async Task GetState_ActiveTask_ShouldExactlyMatchLiveTaskPlanAndStepChanged()
    {
        var task = BuildConvergenceTask();
        task.Steps[0].Kind = NyxIdChatStepKind.Tool;
        task.Steps[0].Operation.Kind = NyxIdChatStepKind.Tool;
        task.Steps[0].Source = new NyxIdChatStepSource
        {
            Tool = new NyxIdChatToolStepSource
            {
                ToolName = "repository_update",
                ServiceSlug = "service-slug-alpha",
                ServiceId = "connected-service-alpha",
                ReadinessCapabilityId = "readiness-capability-alpha",
                ProviderResourceId = "repository-alpha",
                Presentation = ToolPresentationDescriptors.Skill(
                    "repository_update",
                    "Repository maintenance",
                    "Update the exact repository.",
                    "repository-maintenance",
                    "remote"),
            },
        };
        task.Steps[1].Kind = NyxIdChatStepKind.Postcondition;
        task.Steps[1].Source = new NyxIdChatStepSource
        {
            Postcondition = new NyxIdChatPostconditionStepSource
            {
                ActionRequestId = "action-alpha",
                Check = "repository.updated",
                ProviderResourceId = "repository-alpha",
            },
        };
        task.Steps[0].ApprovalObservation.TerminalOutcome =
            NyxIdApprovalTerminalOutcome.Rejected;
        task.Steps[0].ApprovalObservation.SubjectKind = "nyxid.user-service";
        task.Steps[0].ApprovalObservation.SubjectId = "user-service-sensitive-alpha";
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            ProgressSequence = 67,
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = task.TurnId,
                TaskId = task.TaskId,
                Status = NyxIdChatTurnStatus.Active,
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = task.TurnId,
                TaskId = task.TaskId,
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = task,
        };

        var frames = NyxIdChatConversationAguiFrameBuilder.BuildStarted(
            state.ConversationActorId,
            state.ActiveTurn.TurnId,
            state);
        var liveFrames = await WriteLiveFramesAsync(frames, state.ActiveTurn.TurnId);
        var liveTask = JsonNode.Parse(liveFrames.Single(frame =>
                frame["custom"]?["name"]?.GetValue<string>() ==
                NyxIdChatConversationAguiFrameBuilder.TaskSnapshotEventName)
            ["custom"]!["payload"]!.ToJsonString())!;
        var changedStep = JsonNode.Parse(liveFrames.Single(frame =>
                frame["custom"]?["name"]?.GetValue<string>() ==
                NyxIdChatConversationAguiFrameBuilder.TaskStepChangedEventName)
            ["custom"]!["payload"]!["step"]!.ToJsonString())!;

        var store = new InMemoryProjectionDocumentStore<
            NyxIdChatConversationCurrentStateDocument,
            string>(static document => document.ActorId);
        var dispatcher = new StoreTaskStateWriteDispatcher(store);
        var projector = new NyxIdChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-07T12:26:00Z")));
        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = state.ConversationActorId,
                ProjectionKind = "nyxid-chat-conversation",
            },
            WrapCommittedState(state));

        var queryPort = new ProjectionNyxIdChatConversationStateQueryPort(store);
        var response = await ExecuteAsync(queryPort, string.Empty);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var responseNode = JsonNode.Parse(response.Body)!.AsObject();
        var snapshot = responseNode["snapshot"]!.AsObject();
        var currentTask = snapshot["activeTask"]!;
        JsonNode.DeepEquals(liveTask, currentTask).Should().BeTrue(
            "live and reconnect paths serialize one public TaskPlan contract");
        JsonNode.DeepEquals(liveTask["steps"]![0], changedStep).Should().BeTrue(
            "task.step.changed uses the same public step contract as task.snapshot");

        snapshot.ContainsKey("activeTurn").Should().BeTrue();
        snapshot["activeTurn"]!.AsObject().ContainsKey("failureCode").Should().BeTrue(
            "the narrow TaskPlan converter must not alter other current-state JSON");
        currentTask["createdAt"]!.GetValue<string>().Should()
            .Be("2026-08-07T12:25:05.949835500Z");
        currentTask["steps"]![0]!["operation"]!["operationGeneration"]!
            .GetValue<long>().Should().Be(1);
        currentTask["steps"]![0]!["operation"]!["latestProgressSequence"]!
            .GetValue<long>().Should().Be(7);
        currentTask["steps"]![0]!["availableActions"]!.AsObject().Count
            .Should().Be(0, "a present all-false message remains a present empty object");
        currentTask["steps"]![1]!.AsObject().ContainsKey("availableActions").Should().BeFalse(
            "an absent message remains absent");
        var defaultOperation = currentTask["steps"]![1]!["operation"]!.AsObject();
        defaultOperation.Count.Should().Be(1,
            "a present operation always exposes its external-state classification");
        defaultOperation["mayChangeExternalState"]!.GetValue<bool>().Should().BeFalse();
        currentTask["steps"]![0]!.AsObject().ContainsKey("retryInputRebuildable")
            .Should().BeFalse();
        currentTask["steps"]![0]!["operation"]!.AsObject().ContainsKey("idempotencyKey")
            .Should().BeFalse();
        currentTask["steps"]![0]!["approvalObservation"]!["approvalRequestId"]!
            .GetValue<string>().Should().Be("approval-alpha");
        currentTask["steps"]![0]!["approvalObservation"]!["decisionMode"]!
            .GetValue<string>().Should().Be("per_request");
        currentTask["steps"]![0]!["approvalObservation"]!["receiptStatus"]!
            .GetValue<string>().Should().Be("approval_required");
        currentTask["steps"]![0]!["approvalObservation"]!["observedAt"]!
            .GetValue<string>().Should().Be("2026-08-07T12:25:10.919334800Z");
        var liveApproval = liveTask["steps"]![0]!["approvalObservation"]!.AsObject();
        liveApproval["terminalOutcome"]!.GetValue<string>().Should().Be(
            "NYX_ID_APPROVAL_TERMINAL_OUTCOME_REJECTED");
        liveApproval["subjectKind"]!.GetValue<string>().Should().Be("nyxid.user-service");
        liveApproval.ContainsKey("subjectId").Should().BeFalse();
        liveTask.ToJsonString().Should().NotContain("user-service-sensitive-alpha");
        var reloadedApproval = currentTask["steps"]![0]!["approvalObservation"]!.AsObject();
        reloadedApproval.ContainsKey("subjectId").Should().BeFalse();
        currentTask.ToJsonString().Should().NotContain("user-service-sensitive-alpha");
        currentTask["steps"]![0]!["source"]!["tool"]!["providerResourceId"]!
            .GetValue<string>().Should().Be("repository-alpha");
        var presentation = currentTask["steps"]![0]!["source"]!["tool"]!["presentation"]!;
        presentation["invocationName"]!.GetValue<string>().Should().Be("repository_update");
        presentation["displayName"]!.GetValue<string>().Should().Be("Repository maintenance");
        presentation["kind"]!.GetValue<string>().Should().Be("skill");
        presentation["availability"]!.GetValue<string>().Should().Be("available");
        presentation["skill"]!["skillName"]!.GetValue<string>().Should()
            .Be("repository-maintenance");
        presentation["skill"]!["source"]!.GetValue<string>().Should().Be("remote");
        currentTask["steps"]![1]!["source"]!["postcondition"]!["providerResourceId"]!
            .GetValue<string>().Should().Be("repository-alpha");
    }

    [Fact]
    public async Task GetState_ShouldReloadConditionGuardAndNumericThresholdFacts()
    {
        var evaluatedAt = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2026-08-09T08:00:00Z"));
        var task = BuildConvergenceTask();
        task.SchemaVersion = 5;
        task.Steps[1].Guard = new NyxIdChatStepGuard
        {
            ConditionStepId = "step-condition",
            RequiredOutcome = NyxIdChatConditionOutcome.True,
        };
        task.Steps.Insert(1, new NyxIdChatTaskStepState
        {
            StepId = "step-condition",
            Order = 2,
            Kind = NyxIdChatStepKind.Condition,
            Status = NyxIdChatStepStatus.Done,
            Required = true,
            Description = "Check the observed value.",
            Source = new NyxIdChatStepSource
            {
                Condition = new NyxIdChatConditionStepSource
                {
                    Condition = new NyxIdChatNumericConditionState
                    {
                        ConditionId = "condition-alpha",
                        SourceInputRequestId = "input-threshold",
                        SuggestedThreshold = 70,
                        EffectiveThreshold = 75,
                        ThresholdOrigin = NyxIdChatThresholdOrigin.UserOverride,
                        ObservedValue = 80,
                        Comparison = NyxIdChatIntegerComparison.Gte,
                        Outcome = NyxIdChatConditionOutcome.True,
                        EvaluatedAt = evaluatedAt.Clone(),
                        GuardedToolName = "repository_update",
                    },
                },
            },
            ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
        });
        task.Steps[2].Order = 3;
        task.Steps[2].DependsOn.Clear();
        task.Steps[2].DependsOn.Add("step-condition");

        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = task.TurnId,
                TaskId = task.TaskId,
                Status = NyxIdChatTurnStatus.Active,
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = task.TurnId,
                TaskId = task.TaskId,
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = task,
            PendingInput = new NyxIdChatPendingInputState
            {
                RequestId = "input-threshold-next",
                TurnId = task.TurnId,
                TaskId = task.TaskId,
                StepId = "step-beta",
                Prompt = "Choose the threshold.",
                AllowFreeText = true,
                NumericThreshold = new NyxIdChatNumericThresholdInputSpec
                {
                    SuggestedValue = 70,
                    MinimumValue = 0,
                    MaximumValue = 100,
                },
            },
            LatestInputResolution = new NyxIdChatInputResolutionState
            {
                RequestId = "input-threshold",
                ClientRequestId = "client-threshold",
                Outcome = NyxIdChatNeedsYouResolutionOutcome.Accepted,
                Answer = new NyxIdChatInputAnswer
                {
                    FreeText =
                        "Party size 4; one vegetarian; SGD 200 total; research only.",
                },
                CommittedAt = evaluatedAt.Clone(),
                NumericThreshold = new NyxIdChatNumericThresholdResolution
                {
                    SuggestedValue = 70,
                    EffectiveValue = 75,
                    Origin = NyxIdChatThresholdOrigin.UserOverride,
                },
            },
        };

        var store = new InMemoryProjectionDocumentStore<
            NyxIdChatConversationCurrentStateDocument,
            string>(static document => document.ActorId);
        var projector = new NyxIdChatConversationCurrentStateProjector(
            new StoreTaskStateWriteDispatcher(store),
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-09T08:01:00Z")));
        await projector.ProjectAsync(
            new StudioMaterializationContext
            {
                RootActorId = state.ConversationActorId,
                ProjectionKind = "nyxid-chat-conversation",
            },
            WrapCommittedState(state));

        var response = await ExecuteAsync(
            new ProjectionNyxIdChatConversationStateQueryPort(store),
            string.Empty);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var snapshot = JsonNode.Parse(response.Body)!["snapshot"]!;
        snapshot["activeTask"]!["schemaVersion"]!.GetValue<int>().Should().Be(5);
        var conditionStep = snapshot["activeTask"]!["steps"]![1]!;
        conditionStep["kind"]!.GetValue<string>().Should().Be("condition");
        var condition = conditionStep["source"]!["condition"]!["condition"]!;
        condition["conditionId"]!.GetValue<string>().Should().Be("condition-alpha");
        condition["sourceInputRequestId"]!.GetValue<string>().Should()
            .Be("input-threshold");
        condition["suggestedThreshold"]!.GetValue<long>().Should().Be(70);
        condition["effectiveThreshold"]!.GetValue<long>().Should().Be(75);
        condition["thresholdOrigin"]!.GetValue<string>().Should().Be("user_override");
        condition["observedValue"]!.GetValue<long>().Should().Be(80);
        condition["comparison"]!.GetValue<string>().Should().Be("gte");
        condition["outcome"]!.GetValue<string>().Should().Be("true");
        condition["guardedToolName"]!.GetValue<string>().Should()
            .Be("repository_update");
        var guarded = snapshot["activeTask"]!["steps"]![2]!;
        guarded["guard"]!["conditionStepId"]!.GetValue<string>().Should()
            .Be("step-condition");
        guarded["guard"]!["requiredOutcome"]!.GetValue<string>().Should().Be("true");
        snapshot["pendingInput"]!["numericThreshold"]!["suggestedValue"]!
            .GetValue<long>().Should().Be(70);
        snapshot["pendingInput"]!["numericThreshold"]!["minimumValue"]!
            .GetValue<long>().Should().Be(0);
        snapshot["pendingInput"]!["numericThreshold"]!["maximumValue"]!
            .GetValue<long>().Should().Be(100);
        snapshot["latestInputResolution"]!["numericThreshold"]!["suggestedValue"]!
            .GetValue<long>().Should().Be(70);
        snapshot["latestInputResolution"]!["numericThreshold"]!["effectiveValue"]!
            .GetValue<long>().Should().Be(75);
        snapshot["latestInputResolution"]!["numericThreshold"]!["origin"]!
            .GetValue<string>().Should().Be("user_override");
        snapshot["latestInputResolution"]!["answer"]!["freeText"]!
            .GetValue<string>().Should().Be(
                "Party size 4; one vegetarian; SGD 200 total; research only.");
        var liveInputChanged = JsonNode.Parse(
            JsonFormatter.Default.Format(state.LatestInputResolution))!;
        JsonNode.DeepEquals(
                liveInputChanged["answer"],
                snapshot["latestInputResolution"]!["answer"])
            .Should().BeTrue(
                "live input.changed and current-state reload expose the same typed answer");
    }

    [Fact]
    public async Task GetState_ShouldReturnCurrentSnapshotFromTypedQueryPort()
    {
        var activeTask = new NyxIdChatConversationTaskSnapshot(
            "task-alpha",
            "turn-alpha",
            "failed",
            "step-alpha",
            null,
            "TOOL_FAILED",
            "The tool failed.",
            null,
            null,
            [
                new NyxIdChatConversationStepSnapshot(
                    "step-alpha",
                    1,
                    "tool",
                    "failed",
                    true,
                    "Update repository.",
                    true,
                    "not_applied",
                    null,
                    null,
                    "TOOL_FAILED",
                    "The tool failed.",
                    false,
                    new NyxIdChatAvailableActionsSnapshot(true, false, false),
                    null,
                    null,
                    new NyxIdChatConversationStepSourceSnapshot(
                        Tool: new NyxIdChatToolStepSourceSnapshot(
                            "repository_update",
                            "service-slug-alpha",
                            "connected-service-alpha",
                            "readiness-capability-alpha",
                            "repository-alpha"))),
                new NyxIdChatConversationStepSnapshot(
                    "step-beta",
                    2,
                    "tool",
                    "failed",
                    false,
                    "Read repository.",
                    false,
                    "not_applied",
                    null,
                    null,
                    "TOOL_FAILED",
                    "The tool failed.",
                    false,
                    new NyxIdChatAvailableActionsSnapshot(true, false, false),
                    null,
                    new NyxIdChatConversationOperationSnapshot(
                        ConversationActorId: "conversation-alpha",
                        TurnId: "turn-alpha",
                        TaskId: "task-alpha",
                        StepId: "step-beta",
                        OperationId: "operation-beta",
                        OperationGeneration: 1,
                        Kind: "tool",
                        Phase: "failed",
                        MayChangeExternalState: false,
                        Idempotent: false,
                        LatestProgressSequence: 0,
                        TerminalCode: "TOOL_FAILED",
                        SafeMessage: "The tool failed.",
                        RequestedAt: null,
                        DispatchedAt: null,
                        CompletedAt: null),
                    new NyxIdChatConversationStepSourceSnapshot(
                        Tool: new NyxIdChatToolStepSourceSnapshot(
                            "repository_read",
                            "service-slug-beta",
                            "connected-service-beta",
                            null))),
            ]);
        var queryPort = new RecordingQueryPort
        {
            Result = NyxIdChatConversationStateQueryResult.Current(new NyxIdChatConversationStateSnapshot(
                "conversation-alpha",
                "scope-alpha",
                8,
                34,
                DateTimeOffset.Parse("2026-07-25T06:20:00Z"),
                new NyxIdChatConversationTurnSnapshot(
                    "turn-alpha", "task-alpha", "active", null, null, null, null),
                null,
                [],
                activeTask,
                new NyxIdChatPendingApprovalSnapshot(
                    ApprovalRequestId: "approval-alpha",
                    TurnId: "turn-alpha",
                    TaskId: "task-alpha",
                    StepId: "step-alpha",
                    ToolName: "service.connect",
                    ExpiresAt: null,
                    AskedAt: DateTimeOffset.Parse("2026-07-25T06:19:00Z"),
                    Action: "connect",
                    Target: "service-alpha",
                    ActorLabel: "Aevatar Assistant",
                    Reversibility: "reversible",
                    GrantBoundary: "nyxid_step_up",
                    NyxIdRequestId: "nyx-request-alpha"),
                [],
                null,
                null,
                null)),
        };

        var response = await ExecuteAsync(
            queryPort,
            "?afterStateVersion=7&turnId=turn-alpha");

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Subject;
        query.ScopeId.Should().Be("scope-alpha");
        query.ActorId.Should().Be("conversation-alpha");
        query.AfterStateVersion.Should().Be(7);
        query.TurnId.Should().Be("turn-alpha");
        using var json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("status").GetString().Should().Be("current");
        json.RootElement.GetProperty("stateVersion").GetInt64().Should().Be(8);
        json.RootElement.GetProperty("turnId").GetString().Should().Be("turn-alpha");
        json.RootElement.GetProperty("snapshot").GetProperty("actorId").GetString()
            .Should().Be("conversation-alpha");
        json.RootElement
            .GetProperty("snapshot")
            .GetProperty("activeTask")
            .TryGetProperty("gate", out _)
            .Should().BeFalse();
        var pendingApproval = json.RootElement
            .GetProperty("snapshot")
            .GetProperty("pendingApproval");
        pendingApproval.GetProperty("nyxidRequestId").GetString()
            .Should().Be("nyx-request-alpha");
        pendingApproval.TryGetProperty("nyxIdRequestId", out _).Should().BeFalse();
        var toolSource = json.RootElement
            .GetProperty("snapshot")
            .GetProperty("activeTask")
            .GetProperty("steps")[0]
            .GetProperty("source")
            .GetProperty("tool");
        toolSource.GetProperty("serviceId").GetString().Should().Be("connected-service-alpha");
        toolSource.GetProperty("serviceSlug").GetString().Should().Be("service-slug-alpha");
        toolSource.GetProperty("readinessCapabilityId").GetString().Should()
            .Be("readiness-capability-alpha");
        toolSource.GetProperty("providerResourceId").GetString().Should()
            .Be("repository-alpha");
        toolSource.TryGetProperty("readiness_capability_id", out _).Should().BeFalse();
        var sourceWithoutReadiness = json.RootElement
            .GetProperty("snapshot")
            .GetProperty("activeTask")
            .GetProperty("steps")[1]
            .GetProperty("source")
            .GetProperty("tool");
        sourceWithoutReadiness.TryGetProperty("readinessCapabilityId", out _).Should().BeFalse();
        var optionalReadStep = json.RootElement
            .GetProperty("snapshot")
            .GetProperty("activeTask")
            .GetProperty("steps")[1];
        optionalReadStep.GetProperty("required").GetBoolean().Should().BeFalse();
        optionalReadStep.GetProperty("mayChangeExternalState").GetBoolean().Should().BeFalse();
        optionalReadStep.GetProperty("operation")
            .GetProperty("mayChangeExternalState").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetState_ShouldExposeFlatReloadableKeyActionParameters()
    {
        var keyCreate = new NyxIdChatActionSnapshot(
            4,
            "action-key-create",
            "turn-alpha",
            "task-alpha",
            "step-key-create",
            "key.create",
            DateTimeOffset.Parse("2026-08-12T04:00:00Z"),
            [],
            null,
            new NyxIdChatActionRequestSnapshot(
                4,
                "conversation-alpha",
                "turn-alpha",
                "task-alpha",
                "step-key-create",
                "action-key-create",
                "key.create",
                new NyxIdChatActionParamsSnapshot(
                    Name: "agent-alpha",
                    Platform: "codex",
                    AllowedServiceIds: ["service-github", "service-lark"])));
        var keyRotate = new NyxIdChatActionSnapshot(
            4,
            "action-key-rotate",
            "turn-alpha",
            "task-alpha",
            "step-key-rotate",
            "key.rotate",
            DateTimeOffset.Parse("2026-08-12T04:01:00Z"),
            [],
            null,
            new NyxIdChatActionRequestSnapshot(
                4,
                "conversation-alpha",
                "turn-alpha",
                "task-alpha",
                "step-key-rotate",
                "action-key-rotate",
                "key.rotate",
                new NyxIdChatActionParamsSnapshot(KeyId: "key-predecessor")));
        var queryPort = new RecordingQueryPort
        {
            Result = NyxIdChatConversationStateQueryResult.Current(
                new NyxIdChatConversationStateSnapshot(
                    ActorId: "conversation-alpha",
                    ScopeId: "scope-alpha",
                    StateVersion: 12,
                    ProgressSequence: 41,
                    UpdatedAt: DateTimeOffset.Parse("2026-08-12T04:02:00Z"),
                    ActiveTurn: null,
                    LatestTurn: null,
                    RecentTerminalTurns: [],
                    ActiveTask: null,
                    PendingApproval: null,
                    PendingActions: [keyCreate],
                    ControlFence: null,
                    LatestControlResult: null,
                    ContinuationAdmission: null,
                    RecentActions: [keyRotate])),
        };

        var response = await ExecuteAsync(queryPort, string.Empty);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(response.Body);
        var snapshot = json.RootElement.GetProperty("snapshot");
        var createParams = snapshot.GetProperty("pendingActions")[0]
            .GetProperty("request")
            .GetProperty("params");
        createParams.GetProperty("name").GetString().Should().Be("agent-alpha");
        createParams.GetProperty("platform").GetString().Should().Be("codex");
        createParams.GetProperty("allowedServiceIds").EnumerateArray()
            .Select(static value => value.GetString())
            .Should().Equal("service-github", "service-lark");
        createParams.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("name", "platform", "allowedServiceIds");
        createParams.TryGetProperty("keyCreate", out _).Should().BeFalse();
        var rotateParams = snapshot.GetProperty("recentActions")[0]
            .GetProperty("request")
            .GetProperty("params");
        rotateParams.GetProperty("keyId").GetString().Should().Be("key-predecessor");
        rotateParams.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("keyId");
        rotateParams.TryGetProperty("keyRotate", out _).Should().BeFalse();
        response.Body.Should().NotContain("fullKey").And.NotContain("keyMaterial");
    }

    [Fact]
    public async Task GetState_ServiceConnectRequest_ShouldOmitUnsetOptionalTypedParameters()
    {
        var serviceConnect = new NyxIdChatActionSnapshot(
            4,
            "action-service-connect",
            "turn-alpha",
            "task-alpha",
            "step-service-connect",
            "service.connect",
            DateTimeOffset.Parse("2026-08-14T01:00:00Z"),
            [],
            null,
            new NyxIdChatActionRequestSnapshot(
                4,
                "conversation-alpha",
                "turn-alpha",
                "task-alpha",
                "step-service-connect",
                "action-service-connect",
                "service.connect",
                new NyxIdChatActionParamsSnapshot(
                    CatalogService: new NyxIdChatCatalogServiceConnectSnapshot(
                        "github",
                        ["repo:read"],
                        ViaNodeId: null,
                        TargetOrgId: null))));
        var queryPort = new RecordingQueryPort
        {
            Result = NyxIdChatConversationStateQueryResult.Current(
                new NyxIdChatConversationStateSnapshot(
                    ActorId: "conversation-alpha",
                    ScopeId: "scope-alpha",
                    StateVersion: 12,
                    ProgressSequence: 41,
                    UpdatedAt: DateTimeOffset.Parse("2026-08-14T01:01:00Z"),
                    ActiveTurn: null,
                    LatestTurn: null,
                    RecentTerminalTurns: [],
                    ActiveTask: null,
                    PendingApproval: null,
                    PendingActions: [serviceConnect],
                    ControlFence: null,
                    LatestControlResult: null,
                    ContinuationAdmission: null)),
        };

        var response = await ExecuteAsync(queryPort, string.Empty);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(response.Body);
        var catalogService = json.RootElement
            .GetProperty("snapshot")
            .GetProperty("pendingActions")[0]
            .GetProperty("request")
            .GetProperty("params")
            .GetProperty("catalogService");
        catalogService.GetProperty("serviceSlug").GetString().Should().Be("github");
        catalogService.GetProperty("requestedScopes").EnumerateArray()
            .Select(static value => value.GetString())
            .Should().Equal("repo:read");
        catalogService.TryGetProperty("viaNodeId", out _).Should().BeFalse();
        catalogService.TryGetProperty("targetOrgId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetState_ShouldReturnReloadRequiredForInvalidNumericCursorWithoutQuerying()
    {
        var queryPort = new RecordingQueryPort();

        var response = await ExecuteAsync(queryPort, "?afterStateVersion=not-a-version");

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        queryPort.Queries.Should().BeEmpty();
        using var json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("status").GetString().Should().Be("reload_required");
        json.RootElement.GetProperty("reasonCode").GetString()
            .Should().Be("invalid_state_version");
    }

    [Fact]
    public async Task GetState_ShouldReturnNotFoundFromReadModelQuery()
    {
        var queryPort = new RecordingQueryPort
        {
            Result = NyxIdChatConversationStateQueryResult.NotFound(),
        };

        var response = await ExecuteAsync(queryPort, string.Empty);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        using var json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("status").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task GetState_ShouldReadConversationCurrentStateWithoutARegistryReplicaJoin()
    {
        var queryPort = new RecordingQueryPort
        {
            Result = NyxIdChatConversationStateQueryResult.NotModified(8, "turn-alpha"),
        };

        var response = await ExecuteAsync(queryPort, string.Empty);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        queryPort.Queries.Should().ContainSingle(query =>
            query.ScopeId == "scope-alpha" &&
            query.ActorId == "conversation-alpha");
    }

    [Fact]
    public async Task GetState_ShouldRejectAuthenticatedScopeMismatchBeforeQuery()
    {
        var queryPort = new RecordingQueryPort();

        var response = await ExecuteAsync(
            queryPort,
            string.Empty,
            authenticatedScopeId: "scope-other");

        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        queryPort.Queries.Should().BeEmpty();
    }

    [Fact]
    public void StateEndpointSource_ShouldStayReadModelOnly()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.State.cs"));

        source.Should().Contain("INyxIdChatConversationStateQueryPort");
        source.Should().NotContain("IGAgentActorRegistryQueryPort");
        source.Should().NotContain("IActorRuntime");
        source.Should().NotContain("IEventStore");
        source.Should().NotContain("INyxIdChatSessionProjectionPort");
        source.Should().NotContain("ActivateAsync");
        source.Should().NotContain("PrimeAsync");
        source.Should().NotContain("EnsureAndAttachLeaseAsync");
    }

    private static NyxIdChatTaskState BuildConvergenceTask()
    {
        var createdAt = new Timestamp
        {
            Seconds = 1_786_105_505,
            Nanos = 949_835_500,
        };
        var updatedAt = new Timestamp
        {
            Seconds = 1_786_105_510,
            Nanos = 919_334_800,
        };
        var operation = new NyxIdChatOperationState
        {
            Key = new NyxIdChatOperationKey
            {
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                OperationId = "operation-alpha",
                OperationGeneration = 1,
            },
            Kind = NyxIdChatStepKind.Llm,
            Phase = NyxIdChatOperationPhase.Succeeded,
            IdempotencyKey = "actor-internal-idempotency-alpha",
            LatestProgressSequence = 7,
            RequestedAt = createdAt.Clone(),
            DispatchedAt = new Timestamp
            {
                Seconds = 1_786_105_506,
                Nanos = 191_562_800,
            },
            CompletedAt = updatedAt.Clone(),
        };
        return new NyxIdChatTaskState
        {
            TaskId = "task-alpha",
            TurnId = "turn-alpha",
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = "step-beta",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            SchemaVersion = 4,
            ActorId = "conversation-alpha",
            PlanId = "plan-alpha",
            PlanRevision = 2,
            Title = "Complete the requested assistant task",
            Steps =
            {
                new NyxIdChatTaskStepState
                {
                    StepId = "step-alpha",
                    Order = 1,
                    Kind = NyxIdChatStepKind.Llm,
                    Status = NyxIdChatStepStatus.Done,
                    Required = true,
                    Description = "Generate the next assistant response.",
                    Source = new NyxIdChatStepSource
                    {
                        Llm = new NyxIdChatLLMStepSource(),
                    },
                    ExternalEffect = NyxIdChatEffectEvidence.NotApplied,
                    Operation = operation,
                    AvailableActions = new NyxIdChatAvailableActions(),
                    UpdatedAt = updatedAt.Clone(),
                    ApprovalObservation = new NyxIdChatPostReturnApprovalObservation
                    {
                        ApprovalRequestId = "approval-alpha",
                        DecisionMode = NyxIdApprovalDecisionMode.PerRequest,
                        ReceiptStatus = AgentToolReceiptStatus.ApprovalRequired,
                        ObservedAt = updatedAt.Clone(),
                    },
                    RetryInputRebuildable = true,
                    AddedBy = NyxIdChatStepAddedBy.Initial,
                },
                new NyxIdChatTaskStepState
                {
                    StepId = "step-beta",
                    Order = 2,
                    Kind = NyxIdChatStepKind.Input,
                    Status = NyxIdChatStepStatus.Waiting,
                    Required = true,
                    Description = "Collect deployment preferences.",
                    Source = new NyxIdChatStepSource
                    {
                        Input = new NyxIdChatInputStepSource
                        {
                            RequestId = "input-alpha",
                        },
                    },
                    ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
                    Operation = new NyxIdChatOperationState(),
                    UpdatedAt = updatedAt.Clone(),
                    AddedBy = NyxIdChatStepAddedBy.Replan,
                    DependsOn = { "step-alpha" },
                    Estimate = new NyxIdChatStepEstimate(),
                    Substeps = { new NyxIdChatSubstepState() },
                },
            },
        };
    }

    private static async Task<IReadOnlyList<JsonNode>> WriteLiveFramesAsync(
        IReadOnlyList<AGUIEvent> frames,
        string messageId)
    {
        var http = new DefaultHttpContext();
        await using var body = new MemoryStream();
        http.Response.Body = body;
        var writer = new NyxIdChatSseWriter(http.Response);
        foreach (var frame in frames)
            await NyxIdChatAguiSseEventWriter.WriteAsync(frame, messageId, writer);

        body.Position = 0;
        var text = await new StreamReader(body).ReadToEndAsync();
        return text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(static frame => frame.Trim())
            .Where(static frame => frame.StartsWith("data: ", StringComparison.Ordinal))
            .Select(static frame => JsonNode.Parse(frame["data: ".Length..])!)
            .ToArray();
    }

    private static EventEnvelope WrapCommittedState(
        NyxIdChatConversationGAgentState state) => new()
    {
        Id = "event-alpha-23",
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-07T12:26:00Z")),
        Route = EnvelopeRouteSemantics.CreateObserverPublication(state.ConversationActorId),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = "event-alpha-23",
                Version = 23,
                EventData = Any.Pack(new NyxIdChatTurnStartedEvent { State = state }),
                Timestamp = Timestamp.FromDateTimeOffset(
                    DateTimeOffset.Parse("2026-08-07T12:26:00Z")),
            },
            StateRoot = Any.Pack(state),
        }),
    };

    private static async Task<(int StatusCode, string Body)> ExecuteAsync(
        INyxIdChatConversationStateQueryPort queryPort,
        string queryString,
        string? authenticatedScopeId = null)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = authenticatedScopeId is null
                        ? "false"
                        : "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment
            {
                EnvironmentName = authenticatedScopeId is null
                    ? Environments.Development
                    : Environments.Production,
            })
            .AddSingleton(queryPort)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        if (authenticatedScopeId is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("scope_id", authenticatedScopeId)],
                authenticationType: "test"));
        }

        context.Request.Method = HttpMethods.Get;
        context.Request.RouteValues = new RouteValueDictionary
        {
            ["scopeId"] = "scope-alpha",
            ["actorId"] = "conversation-alpha",
        };
        context.Request.QueryString = new QueryString(queryString);
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await BuildRouteEndpoint().RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (
            context.Response.StatusCode,
            await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static RouteEndpoint BuildRouteEndpoint()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        var app = builder.Build();
        app.MapNyxIdChatEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => string.Equals(
                endpoint.RoutePattern.RawText,
                StateRoute,
                StringComparison.Ordinal));
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root could not be resolved.");
    }

    private sealed class StoreTaskStateWriteDispatcher(
        InMemoryProjectionDocumentStore<NyxIdChatConversationCurrentStateDocument, string> store)
        : IProjectionWriteDispatcher<NyxIdChatConversationCurrentStateDocument>
    {
        public Task<ProjectionWriteResult> UpsertAsync(
            NyxIdChatConversationCurrentStateDocument readModel,
            CancellationToken ct = default) => store.UpsertAsync(readModel, ct);

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default) => store.DeleteAsync(id, ct);
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingQueryPort : INyxIdChatConversationStateQueryPort
    {
        public NyxIdChatConversationStateQueryResult Result { get; init; } =
            NyxIdChatConversationStateQueryResult.ReloadRequired(
                0,
                null,
                "unconfigured_test_result");
        public List<NyxIdChatConversationStateQuery> Queries { get; } = [];

        public Task<NyxIdChatConversationStateQueryResult> GetAsync(
            NyxIdChatConversationStateQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>
            GetAttentionSummariesAsync(
                string scopeId,
                IReadOnlyCollection<string> actorIds,
                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>(
                new Dictionary<string, NyxIdChatConversationAttentionSummary>());
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.AI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
