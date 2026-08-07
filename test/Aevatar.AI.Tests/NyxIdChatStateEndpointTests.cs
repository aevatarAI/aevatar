using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AGUI.Contracts;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
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

        var dispatcher = new RecordingTaskStateWriteDispatcher();
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

        dispatcher.Document.Should().NotBeNull();
        var document = dispatcher.Document!;
        var queryPort = new ProjectionNyxIdChatConversationStateQueryPort(
            new SingleTaskStateDocumentReader(document));
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
        currentTask["steps"]![1]!["operation"]!.AsObject().Count.Should().Be(0,
            "a present empty operation remains a present empty object");
        currentTask["steps"]![0]!.AsObject().ContainsKey("retryInputRebuildable")
            .Should().BeFalse();
        currentTask["steps"]![0]!["operation"]!.AsObject().ContainsKey("idempotencyKey")
            .Should().BeFalse();
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
                            "readiness-capability-alpha"))),
                new NyxIdChatConversationStepSnapshot(
                    "step-beta",
                    2,
                    "tool",
                    "failed",
                    true,
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
                    null,
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
        toolSource.TryGetProperty("readiness_capability_id", out _).Should().BeFalse();
        var sourceWithoutReadiness = json.RootElement
            .GetProperty("snapshot")
            .GetProperty("activeTask")
            .GetProperty("steps")[1]
            .GetProperty("source")
            .GetProperty("tool");
        sourceWithoutReadiness.TryGetProperty("readinessCapabilityId", out _).Should().BeFalse();
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
    public async Task GetState_ShouldNotReadConversationStateWhenRegistryDoesNotOwnActor()
    {
        var queryPort = new RecordingQueryPort();
        var registry = new RecordingRegistryQueryPort
        {
            Snapshot = new GAgentActorRegistrySnapshot(
                "scope-alpha",
                [new GAgentActorGroup(NyxIdChatServiceDefaults.GAgentKind, ["conversation-other"])],
                3,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
        };

        var response = await ExecuteAsync(
            queryPort,
            string.Empty,
            registryQueryPort: registry);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        queryPort.Queries.Should().BeEmpty();
        registry.ScopeIds.Should().ContainSingle("scope-alpha");
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
            Gate = new NyxIdChatPlanGate { Mode = NyxIdChatPlanGateMode.Auto },
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
        string? authenticatedScopeId = null,
        IGAgentActorRegistryQueryPort? registryQueryPort = null)
    {
        registryQueryPort ??= RecordingRegistryQueryPort.OwningConversation();
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
            .AddSingleton(registryQueryPort)
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

    private sealed class RecordingTaskStateWriteDispatcher
        : IProjectionWriteDispatcher<NyxIdChatConversationCurrentStateDocument>
    {
        public NyxIdChatConversationCurrentStateDocument? Document { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(
            NyxIdChatConversationCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Document = readModel.Clone();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SingleTaskStateDocumentReader(
        NyxIdChatConversationCurrentStateDocument document)
        : IProjectionDocumentReader<NyxIdChatConversationCurrentStateDocument, string>
    {
        public Task<NyxIdChatConversationCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<NyxIdChatConversationCurrentStateDocument?>(
                document.Clone());
        }

        public Task<ProjectionDocumentQueryResult<NyxIdChatConversationCurrentStateDocument>>
            QueryAsync(
                ProjectionDocumentQuery query,
                CancellationToken ct = default) =>
            throw new NotSupportedException();
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

    private sealed class RecordingRegistryQueryPort : IGAgentActorRegistryQueryPort
    {
        public GAgentActorRegistrySnapshot Snapshot { get; init; } =
            new(
                "scope-alpha",
                [],
                0,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue);
        public List<string> ScopeIds { get; } = [];

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScopeIds.Add(scopeId);
            return Task.FromResult(Snapshot);
        }

        public static RecordingRegistryQueryPort OwningConversation() => new()
        {
            Snapshot = new GAgentActorRegistrySnapshot(
                "scope-alpha",
                [
                    new GAgentActorGroup(
                        NyxIdChatServiceDefaults.GAgentKind,
                        ["conversation-alpha"]),
                ],
                4,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
        };
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
