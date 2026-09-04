using System.Security.Claims;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Mainnet.Host.Api.Chat;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetChatEndpointsTests
{
    [Theory]
    [InlineData("{}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"prompt\":\"hello\"}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"workflow\":\"direct\"}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"workflowYamls\":[\"name: inline\"]}", "ExternalWorkflowCompatibility")]
    [InlineData("{\"type\":\"text\"}", "Assistant")]
    [InlineData("{\"type\":\"input.resolve\"}", "Assistant")]
    [InlineData("{\"type\":\"text\",\"workflow\":\"studio\"}", "Assistant")]
    [InlineData("{\"type\":\"action.continue\"}", "Assistant")]
    [InlineData("{\"type\":\"approval.resolve\"}", "Assistant")]
    [InlineData("{\"type\":\"plan.resolve\"}", "Unsupported")]
    [InlineData("{\"type\":\"task.stop\"}", "Assistant")]
    [InlineData("{\"type\":\"task.steer\"}", "Assistant")]
    [InlineData("{\"type\":\"step.retry\"}", "Assistant")]
    [InlineData("{\"type\":\"step.skip\"}", "Assistant")]
    [InlineData("{\"type\":\"future.type\"}", "Unsupported")]
    public async Task RequestShape_ShouldSelectOneExplicitBoundary(
        string json,
        string expected)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.ToString().Should().Be(expected);
        if (expected == "ExternalWorkflowCompatibility")
        {
            http.Request.Body.Position.Should().Be(0);
            using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
            (await reader.ReadToEndAsync()).Should().Be(json);
        }
    }

    [Fact]
    public async Task ExplicitStudioWorkflowJson_ShouldUseFrozenCompatibilityAdapter()
    {
        const string json = "{\"commandId\":\"cmd-1\",\"conversation\":{\"conversationId\":null},\"prompt\":\"hello\",\"sessionId\":\"session-1\",\"workflow\":\"studio\"}";
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.ExternalWorkflowCompatibility);
        http.Request.Body.Position.Should().Be(0);
        using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
        (await reader.ReadToEndAsync()).Should().Be(json);
    }

    [Fact]
    public async Task Multipart_ShouldRemainInFrozenExternalCompatibilityAdapterWithoutReadingBody()
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=test";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("unchanged"));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.ExternalWorkflowCompatibility);
        http.Request.Body.Position.Should().Be(0);
    }

    [Theory]
    [InlineData("text/plain", "hello")]
    [InlineData("application/json", "not-json")]
    [InlineData("application/json", "[]")]
    public async Task UnsupportedOrMalformedInput_ShouldNotFallThroughToWorkflow(
        string contentType,
        string body)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = contentType;
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        var result = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        result.Kind.Should().Be(MainnetChatRequestKind.Unsupported);
    }

    [Fact]
    public async Task AssistantText_ShouldRouteWaitingWorkflowConversationToSignalBeforeNyxIdChat()
    {
        const string json = "{\"type\":\"text\",\"conversationId\":\"nyxid-chat-1\",\"clientRequestId\":\"client-1\",\"prompt\":\"Let's pick the first restaurant, Pasta Bar.\"}";
        var recoveryPort = new RecordingChatHistoryCreateRecoveryReadPort
        {
            Recovery = new WorkflowChatHistoryCreateRecovery(
                WorkflowChatHistoryCreateRecoveryStatus.Bound,
                "scope-1",
                "create-command",
                "nyxid-chat-1",
                "turn-1",
                "workflow-actor-1",
                "create-command",
                "create-correlation",
                "fingerprint",
                7,
                DateTimeOffset.UtcNow),
        };
        var currentStateQueryPort = new FixedWorkflowExecutionCurrentStateQueryPort(new WorkflowActorSnapshot
        {
            ActorId = "workflow-actor-1",
            ScopeId = "scope-1",
            RunId = "run-1",
            CompletionStatus = WorkflowRunCompletionStatus.WaitingForSignal,
            ActivityWaiting = new WorkflowRunActivityWaitingSnapshot
            {
                Availability = "available",
                WaitingKind = "signal",
                StepId = "wait_for_post_timeout_choice",
                Prompt = "dinner_date_user_choice_after_timeout",
            },
        });
        var signalDispatchService = new RecordingWorkflowSignalDispatchService();
        var chatStateQueryPort = new FixedNyxIdChatConversationStateQueryPort(CreatePendingWorkflowSignalState());
        var workflowQueryService = new FixedWorkflowExecutionQueryApplicationService(new WorkflowRunReport
        {
            CurrentWaitingSignal = new WorkflowRunWaitingSignal
            {
                RunId = "run-1",
                StepId = "wait_for_post_timeout_choice",
                SignalName = "dinner_date_user_choice_after_timeout",
                Prompt = "Waiting for user to choose one held dinner option",
                TimeoutMs = 600000,
            },
        });
        var workflowSignalAcceptancePort = new RecordingWorkflowSignalAcceptancePort();
        var http = CreateAuthenticatedJsonContext(
            json,
            services =>
            {
                services.AddSingleton<IWorkflowChatHistoryCreateRecoveryReadPort>(recoveryPort);
                services.AddSingleton<IWorkflowExecutionCurrentStateQueryPort>(currentStateQueryPort);
                services.AddSingleton<IWorkflowExecutionQueryApplicationService>(workflowQueryService);
                services.AddSingleton<INyxIdChatConversationStateQueryPort>(chatStateQueryPort);
                services.AddSingleton<INyxIdChatWorkflowSignalAcceptancePort>(workflowSignalAcceptancePort);
                services.AddSingleton<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(signalDispatchService);
            });
        var classification = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        var handled = await MainnetChatEndpoints.TryHandleWorkflowSignalContinuationAsync(
            http,
            classification.Body!.Value,
            CancellationToken.None);

        handled.Should().BeTrue();
        recoveryPort.ConversationRequests.Should().ContainSingle().Which.Should().Be(("scope-1", "nyxid-chat-1"));
        currentStateQueryPort.ActorIds.Should().ContainSingle().Which.Should().Be("workflow-actor-1");
        var command = signalDispatchService.Commands.Should().ContainSingle().Subject;
        command.ActorId.Should().Be("workflow-actor-1");
        command.RunId.Should().Be("run-1");
        command.SignalName.Should().Be("dinner_date_user_choice_after_timeout");
        command.StepId.Should().Be("wait_for_post_timeout_choice");
        command.CommandId.Should().Be("client-1");
        command.Payload.Should().Be("Let's pick the first restaurant, Pasta Bar.");
        command.CorrelationId.Should().Be("client-1");
        chatStateQueryPort.Queries.Should().ContainSingle().Which.Should().Match<NyxIdChatConversationStateQuery>(query =>
            query.ScopeId == "scope-1" && query.ActorId == "nyxid-chat-1");
        var clearCommand = workflowSignalAcceptancePort.Commands.Should().ContainSingle().Subject;
        clearCommand.ScopeId.Should().Be("scope-1");
        clearCommand.ConversationActorId.Should().Be("nyxid-chat-1");
        clearCommand.WorkflowActorId.Should().Be("workflow-actor-1");
        clearCommand.RunId.Should().Be("run-1");
        clearCommand.SignalName.Should().Be("dinner_date_user_choice_after_timeout");
        clearCommand.StepId.Should().Be("wait_for_post_timeout_choice");
        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task WorkflowSignalContinuation_ShouldPersistTimeoutHoldNoticeBeforePostTimeoutChoice()
    {
        const string json = """
            {
              "type":"text",
              "conversationId":"nyxid-chat-1",
              "prompt":"1",
              "clientRequestId":"client-1"
            }
            """;
        var recoveryPort = new RecordingChatHistoryCreateRecoveryReadPort
        {
            Recovery = new WorkflowChatHistoryCreateRecovery(
                WorkflowChatHistoryCreateRecoveryStatus.Bound,
                "scope-1",
                "create-command",
                "nyxid-chat-1",
                "turn-1",
                "workflow-actor-1",
                "create-command",
                "create-correlation",
                "fingerprint",
                7,
                DateTimeOffset.UtcNow),
        };
        var waitingSnapshot = new WorkflowActorSnapshot
        {
            ActorId = "workflow-actor-1",
            ScopeId = "scope-1",
            RunId = "run-1",
            CompletionStatus = WorkflowRunCompletionStatus.WaitingForSignal,
            ActivityWaiting = new WorkflowRunActivityWaitingSnapshot
            {
                Availability = "available",
                WaitingKind = "signal",
                StepId = "wait_for_post_timeout_choice",
                Prompt = "Three venues are held after timeout. Please choose one held dinner option.",
            },
        };
        var completedSnapshot = new WorkflowActorSnapshot
        {
            ActorId = "workflow-actor-1",
            ScopeId = "scope-1",
            RunId = "run-1",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            LastOutput = "{\"kept\":\"Pasta Bar\"}",
        };
        var currentStateQueryPort = new SequencedWorkflowExecutionCurrentStateQueryPort(waitingSnapshot, completedSnapshot);
        var signalDispatchService = new RecordingWorkflowSignalDispatchService();
        var chatStateQueryPort = new FixedNyxIdChatConversationStateQueryPort(null);
        var workflowQueryService = new FixedWorkflowExecutionQueryApplicationService(new WorkflowRunReport
        {
            CurrentWaitingSignal = new WorkflowRunWaitingSignal
            {
                RunId = "run-1",
                StepId = "wait_for_post_timeout_choice",
                SignalName = "dinner_date_user_choice_after_timeout",
                Prompt = "Three venues are held after timeout. Please choose one held dinner option. 1. Pasta Bar 2. Tipo Pasta Bar 3. Kamoshita",
                TimeoutMs = 600000,
            },
        });
        var workflowSignalAcceptancePort = new RecordingWorkflowSignalAcceptancePort();
        var chatHistoryCommandPort = new RecordingChatHistoryCommandPort();
        var http = CreateAuthenticatedJsonContext(
            json,
            services =>
            {
                services.AddSingleton<IWorkflowChatHistoryCreateRecoveryReadPort>(recoveryPort);
                services.AddSingleton<IWorkflowExecutionCurrentStateQueryPort>(currentStateQueryPort);
                services.AddSingleton<IWorkflowExecutionQueryApplicationService>(workflowQueryService);
                services.AddSingleton<INyxIdChatConversationStateQueryPort>(chatStateQueryPort);
                services.AddSingleton<INyxIdChatWorkflowSignalAcceptancePort>(workflowSignalAcceptancePort);
                services.AddSingleton<IChatHistoryCommandPort>(chatHistoryCommandPort);
                services.AddSingleton<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(signalDispatchService);
            });
        var classification = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        var handled = await MainnetChatEndpoints.TryHandleWorkflowSignalContinuationAsync(
            http,
            classification.Body!.Value,
            CancellationToken.None);

        handled.Should().BeTrue();
        chatHistoryCommandPort.Reservations.Should().HaveCount(2);
        chatHistoryCommandPort.TerminalNotifications.Should().HaveCount(2);
        chatHistoryCommandPort.Reservations[0].DeliveryId.Should()
            .Be("workflow-timeout-hold:scope-1:nyxid-chat-1:workflow-actor-1:run-1:wait_for_post_timeout_choice");
        chatHistoryCommandPort.Reservations[0].UserText.Should().BeEmpty();
        chatHistoryCommandPort.TerminalNotifications[0].Text.Should().Contain("Three venues are held after timeout");
        chatHistoryCommandPort.TerminalNotifications[0].Text.Should().Contain("Pasta Bar");
        chatHistoryCommandPort.TerminalNotifications[0].Text.Should().Contain("Tipo Pasta Bar");
        chatHistoryCommandPort.TerminalNotifications[0].Text.Should().Contain("Kamoshita");
        chatHistoryCommandPort.Reservations[1].DeliveryId.Should()
            .Be("workflow-signal:scope-1:nyxid-chat-1:client-1");
        chatHistoryCommandPort.Reservations[1].UserText.Should().Be("1");
        chatHistoryCommandPort.TerminalNotifications[1].Text.Should().Be("Pasta Bar is selected.");
    }

    [Fact]
    public async Task WorkflowSignalContinuation_ShouldRouteFromRecoveryWhenConversationHasNoPendingWorkflowSignal()
    {
        const string json = """
            {
              "type":"text",
              "conversationId":"nyxid-chat-1",
              "prompt":"1",
              "clientRequestId":"client-1"
            }
            """;
        var recoveryPort = new RecordingChatHistoryCreateRecoveryReadPort
        {
            Recovery = new WorkflowChatHistoryCreateRecovery(
                WorkflowChatHistoryCreateRecoveryStatus.Bound,
                "scope-1",
                "create-command",
                "nyxid-chat-1",
                "turn-1",
                "workflow-actor-1",
                "create-command",
                "create-correlation",
                "fingerprint",
                7,
                DateTimeOffset.UtcNow),
        };
        var currentStateQueryPort = new FixedWorkflowExecutionCurrentStateQueryPort(new WorkflowActorSnapshot
        {
            ActorId = "workflow-actor-1",
            ScopeId = "scope-1",
            RunId = "run-1",
            CompletionStatus = WorkflowRunCompletionStatus.WaitingForSignal,
            ActivityWaiting = new WorkflowRunActivityWaitingSnapshot
            {
                Availability = "available",
                WaitingKind = "signal",
                StepId = "wait_for_post_timeout_choice",
                Prompt = "dinner_date_user_choice_after_timeout",
            },
        });
        var signalDispatchService = new RecordingWorkflowSignalDispatchService();
        var chatStateQueryPort = new FixedNyxIdChatConversationStateQueryPort(null);
        var workflowQueryService = new FixedWorkflowExecutionQueryApplicationService(new WorkflowRunReport
        {
            CurrentWaitingSignal = new WorkflowRunWaitingSignal
            {
                RunId = "run-1",
                StepId = "wait_for_post_timeout_choice",
                SignalName = "dinner_date_user_choice_after_timeout",
                Prompt = "Waiting for user to choose one held dinner option",
                TimeoutMs = 600000,
            },
        });
        var workflowSignalAcceptancePort = new RecordingWorkflowSignalAcceptancePort();
        var http = CreateAuthenticatedJsonContext(
            json,
            services =>
            {
                services.AddSingleton<IWorkflowChatHistoryCreateRecoveryReadPort>(recoveryPort);
                services.AddSingleton<IWorkflowExecutionCurrentStateQueryPort>(currentStateQueryPort);
                services.AddSingleton<IWorkflowExecutionQueryApplicationService>(workflowQueryService);
                services.AddSingleton<INyxIdChatConversationStateQueryPort>(chatStateQueryPort);
                services.AddSingleton<INyxIdChatWorkflowSignalAcceptancePort>(workflowSignalAcceptancePort);
                services.AddSingleton<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(signalDispatchService);
            });
        var classification = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        var handled = await MainnetChatEndpoints.TryHandleWorkflowSignalContinuationAsync(
            http,
            classification.Body!.Value,
            CancellationToken.None);

        handled.Should().BeTrue();
        chatStateQueryPort.Queries.Should().ContainSingle().Which.Should().Match<NyxIdChatConversationStateQuery>(query =>
            query.ScopeId == "scope-1" && query.ActorId == "nyxid-chat-1");
        var command = signalDispatchService.Commands.Should().ContainSingle().Subject;
        command.ActorId.Should().Be("workflow-actor-1");
        command.RunId.Should().Be("run-1");
        command.SignalName.Should().Be("dinner_date_user_choice_after_timeout");
        command.StepId.Should().Be("wait_for_post_timeout_choice");
        command.CommandId.Should().Be("client-1");
        command.Payload.Should().Be("1");
        workflowSignalAcceptancePort.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task ExternalWorkflowCompatibility_ShouldPassResolvedWorkflowBindingToWorkflowHandler()
    {
        const string json = "{\"prompt\":\"hello\",\"workflow\":\"dinner_date\"}";
        var workflow = CreateDinnerWorkflowSummary();
        var ensurePort = new RecordingScopeWorkflowTemplateEnsurePort(
            ScopeWorkflowTemplateEnsureResult.AlreadyCurrent(workflow, workflow.ActiveRevisionId));
        var resolvePort = new RecordingScopeWorkflowDefinitionBindingResolvePort(
            ScopeWorkflowDefinitionBindingResolveResult.Resolved(
                "scope-1",
                "dinner_date",
                new WorkflowDefinitionBinding(
                    "actor-dinner",
                    "dinner_date_mock",
                    "name: dinner_date_mock\nsteps: []",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ExternalCapabilityExecutionMode.Interactive,
                    "scope-1",
                    WorkflowRunOrigins.AdHocChat,
                    SourceKind: "service_revision",
                    WorkflowId: "dinner_date",
                    RevisionId: "dinner-date-mock-v2",
                    DefinitionVersion: 7)));
        var chatRunService = new RecordingWorkflowChatRunInteractionPort();
        var http = CreateAuthenticatedJsonContext(
            json,
            services =>
            {
                services.AddSingleton<IScopeWorkflowTemplateEnsurePort>(ensurePort);
                services.AddSingleton<IScopeWorkflowDefinitionBindingResolvePort>(resolvePort);
                services.AddSingleton<IWorkflowChatRunInteractionPort>(chatRunService);
            });
        var classification = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        await ExternalWorkflowChatCompatibilityAdapter.HandleAsync(http, classification.Body, CancellationToken.None);

        ensurePort.Requests.Should().ContainSingle().Which.WorkflowId.Should().Be("dinner_date");
        resolvePort.Requests.Should().ContainSingle().Which.Should().Be(("scope-1", "dinner_date"));
        chatRunService.LastRequest.Should().NotBeNull();
        chatRunService.LastRequest!.Source.CatalogName!.WorkflowName.Should().Be("dinner_date_mock");
        chatRunService.LastRequest.ResolvedDefinitionBinding.Should().NotBeNull();
        chatRunService.LastRequest.ResolvedDefinitionBinding!.DefinitionActorId.Should().Be("actor-dinner");
        chatRunService.LastRequest.ResolvedDefinitionBinding.WorkflowId.Should().Be("dinner_date");
        chatRunService.LastRequest.ResolvedDefinitionBinding.WorkflowName.Should().Be("dinner_date_mock");
        chatRunService.LastRequest.ResolvedDefinitionBinding.DefinitionVersion.Should().Be(7);
    }

    [Fact]
    public async Task ExternalWorkflowCompatibility_ShouldEnsureConfiguredTemplateBeforeWorkflowHandler()
    {
        const string json = "{\"prompt\":\"hello\",\"workflow\":\"dinner_date\"}";
        var ensurePort = new RecordingScopeWorkflowTemplateEnsurePort(
            ScopeWorkflowTemplateEnsureResult.Failed(
                "scope-1",
                "dinner_date",
                "dinner-date-mock-v2",
                "workflow_template_readmodel_not_observed"));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
            })
            .Build());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);
        services.AddSingleton<IScopeWorkflowTemplateEnsurePort>(ensurePort);
        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("scope_id", "scope-1"),
                    new Claim("sub", "caller-1"),
                ],
                authenticationType: "test")),
        };
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var classification = await MainnetChatEndpoints.ClassifyRequestAsync(http.Request);

        await ExternalWorkflowChatCompatibilityAdapter.HandleAsync(http, classification.Body, CancellationToken.None);

        ensurePort.Requests.Should().ContainSingle().Which.Should().Match<ScopeWorkflowTemplateEnsureRequest>(request =>
            request.ScopeId == "scope-1" && request.WorkflowId == "dinner_date");
        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    private static DefaultHttpContext CreateAuthenticatedJsonContext(
        string json,
        Action<ServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
            })
            .Build());
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);
        configureServices?.Invoke(services);

        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("scope_id", "scope-1"),
                new Claim("sub", "caller-1"),
            ], authenticationType: "test")),
        };
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Headers.Authorization = "Bearer caller-token";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static ScopeWorkflowSummary CreateDinnerWorkflowSummary() =>
        new(
            "scope-1",
            "dinner_date",
            "Dinner Date Mock",
            "scope-1:default:default:default",
            "dinner_date_mock",
            "actor-dinner",
            "dinner-date-mock-v2",
            "deployment-dinner",
            "Active",
            DateTimeOffset.UtcNow)
        {
            ServiceAppId = "default",
            ServiceNamespace = "default",
            PublishedServiceId = "default",
        };

    private sealed class RecordingChatHistoryCreateRecoveryReadPort : IWorkflowChatHistoryCreateRecoveryReadPort
    {
        public WorkflowChatHistoryCreateRecovery? Recovery { get; init; }

        public List<(string ScopeId, string CommandId)> Requests { get; } = [];

        public List<(string ScopeId, string ConversationId)> ConversationRequests { get; } = [];

        public Task<WorkflowChatHistoryCreateRecovery?> GetAsync(
            string scopeId,
            string commandId,
            CancellationToken ct = default)
        {
            Requests.Add((scopeId, commandId));
            return Task.FromResult(Recovery);
        }

        public Task<WorkflowChatHistoryCreateRecovery?> GetByConversationAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default)
        {
            ConversationRequests.Add((scopeId, conversationId));
            return Task.FromResult(Recovery);
        }
    }

    private sealed class FixedWorkflowExecutionCurrentStateQueryPort(
        WorkflowActorSnapshot? snapshot) : IWorkflowExecutionCurrentStateQueryPort
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;

        public List<string> ActorIds { get; } = [];

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
            string actorId,
            CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);

        public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorProjectionState?>(null);
    }

    private sealed class SequencedWorkflowExecutionCurrentStateQueryPort(
        params WorkflowActorSnapshot?[] snapshots) : IWorkflowExecutionCurrentStateQueryPort
    {
        private int _index;

        public bool WorkflowActorCurrentStateQueryEnabled => true;

        public List<string> ActorIds { get; } = [];

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
            string actorId,
            CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            var snapshot = _index < snapshots.Length
                ? snapshots[_index]
                : snapshots.LastOrDefault();
            _index++;
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);

        public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorProjectionState?>(null);
    }

    private static NyxIdChatConversationStateQueryResult CreatePendingWorkflowSignalState() =>
        NyxIdChatConversationStateQueryResult.Current(new NyxIdChatConversationStateSnapshot(
            "nyxid-chat-1",
            "scope-1",
            9,
            1,
            DateTimeOffset.UtcNow,
            null,
            null,
            [],
            null,
            null,
            [],
            null,
            null,
            null,
            PendingWorkflowSignal: new NyxIdChatPendingWorkflowSignalSnapshot(
                "workflow-actor-1",
                "run-1",
                "dinner_date_user_choice",
                "wait_for_user_choice_timeout",
                "Waiting for user to choose one dinner option before automatic holds",
                10000,
                DateTimeOffset.UtcNow)));

    private sealed class FixedNyxIdChatConversationStateQueryPort(
        NyxIdChatConversationStateQueryResult? result) : INyxIdChatConversationStateQueryPort
    {
        public List<NyxIdChatConversationStateQuery> Queries { get; } = [];

        public Task<NyxIdChatConversationStateQueryResult> GetAsync(
            NyxIdChatConversationStateQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(result ?? NyxIdChatConversationStateQueryResult.Current(
                new NyxIdChatConversationStateSnapshot(
                    query.ActorId,
                    query.ScopeId,
                    9,
                    1,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    [],
                    null,
                    null,
                    [],
                    null,
                    null,
                    null)));
        }

        public Task<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>
            GetAttentionSummariesAsync(
                string scopeId,
                IReadOnlyCollection<string> actorIds,
                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>(
                new Dictionary<string, NyxIdChatConversationAttentionSummary>());
    }

    private sealed class FixedWorkflowExecutionQueryApplicationService(WorkflowRunReport? report)
        : IWorkflowExecutionQueryApplicationService
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;

        public List<string> ReportActorIds { get; } = [];

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);

        public IReadOnlyList<string> ListWorkflows() => [];

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowCatalogItem>>([]);

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default) =>
            Task.FromResult<WorkflowCatalogItemDetail?>(null);

        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCapabilitiesDocument());

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowActorSnapshot?>(null);

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string workflowRunId, CancellationToken ct = default)
        {
            ReportActorIds.Add(workflowRunId);
            return Task.FromResult(report);
        }

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(
            string workflowRunId,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunTimelineExportItem>>([]);

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(
            string workflowRunId,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowRunGraphExportEdge>>([]);

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(
            string workflowRunId,
            int depth = 2,
            int take = 200,
            WorkflowRunGraphExportQueryOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowRunGraphExportSubgraph());
    }

    private sealed class RecordingWorkflowSignalAcceptancePort : INyxIdChatWorkflowSignalAcceptancePort
    {
        public List<NyxIdChatWorkflowSignalAcceptedCommand> Commands { get; } = [];

        public Task MarkAcceptedAsync(
            NyxIdChatWorkflowSignalAcceptedCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingChatHistoryCommandPort : IChatHistoryCommandPort
    {
        public List<ChatHistoryConversationInitialization> ConversationInitializations { get; } = [];

        public List<ChatHistoryTurnDeliveryReservation> Reservations { get; } = [];

        public List<ChatHistoryTurnTerminalNotification> TerminalNotifications { get; } = [];

        public List<IReadOnlyList<StoredChatMessage>> SavedMessageBatches { get; } = [];

        public Task InitializeConversationAsync(
            ChatHistoryConversationInitialization request,
            CancellationToken ct = default)
        {
            ConversationInitializations.Add(request);
            return Task.CompletedTask;
        }

        public Task ReserveTurnDeliveryAsync(
            ChatHistoryTurnDeliveryReservation request,
            CancellationToken ct = default)
        {
            Reservations.Add(request);
            return Task.CompletedTask;
        }

        public Task NotifyTurnTerminalAsync(
            ChatHistoryTurnTerminalNotification notification,
            CancellationToken ct = default)
        {
            TerminalNotifications.Add(notification);
            return Task.CompletedTask;
        }

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default)
        {
            SavedMessageBatches.Add(messages);
            return Task.CompletedTask;
        }

        public Task<ChatHistoryDeleteResult> DeleteConversationAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default) =>
            Task.FromResult(ChatHistoryDeleteResult.Accepted());
    }

    private sealed class RecordingWorkflowSignalDispatchService
        : ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public List<WorkflowSignalCommand> Commands { get; } = [];

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowSignalCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
                .Success(new WorkflowRunControlAcceptedReceipt(
                    command.ActorId,
                    command.RunId,
                    command.CommandId ?? "accepted-command",
                    command.CorrelationId ?? command.CommandId ?? "accepted-correlation")));
        }
    }

    private sealed class RecordingWorkflowChatRunInteractionPort : IWorkflowChatRunInteractionPort
    {
        public WorkflowChatRunRequest? LastRequest { get; private set; }

        public Task<WorkflowChatRunInteractionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(WorkflowChatRunInteractionResult.Failure(
                WorkflowChatRunStartError.WorkflowBindingMismatch));
        }
    }

    private sealed class RecordingScopeWorkflowDefinitionBindingResolvePort(
        ScopeWorkflowDefinitionBindingResolveResult result) : IScopeWorkflowDefinitionBindingResolvePort
    {
        public List<(string ScopeId, string WorkflowId)> Requests { get; } = [];

        public Task<ScopeWorkflowDefinitionBindingResolveResult> ResolveAsync(
            ScopeWorkflowDefinitionBindingResolveRequest request,
            CancellationToken ct = default)
        {
            Requests.Add((request.ScopeId, request.WorkflowId));
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingScopeWorkflowTemplateEnsurePort(
        ScopeWorkflowTemplateEnsureResult result) : IScopeWorkflowTemplateEnsurePort
    {
        public List<ScopeWorkflowTemplateEnsureRequest> Requests { get; } = [];

        public Task<ScopeWorkflowTemplateEnsureResult> EnsureAsync(
            ScopeWorkflowTemplateEnsureRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
