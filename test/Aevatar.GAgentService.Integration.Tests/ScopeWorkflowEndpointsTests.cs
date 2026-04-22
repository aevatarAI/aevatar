using System.Text;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeWorkflowEndpointsTests
{
    [Fact]
    public async Task HandleUpsertWorkflowAsync_ShouldReturnBadRequest_WhenServiceRejectsRequest()
    {
        var http = CreateHttpContext();
        var result = await ScopeWorkflowEndpoints.HandleUpsertWorkflowAsync(
            http,
            "user-1",
            "approval",
            new ScopeWorkflowEndpoints.UpsertScopeWorkflowHttpRequest(string.Empty),
            BuildCommandPort(),
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("WorkflowYaml is required");
    }

    [Fact]
    public async Task HandleRunWorkflowStreamAsync_ShouldReturnNotFound_WhenActorDoesNotBelongToUser()
    {
        var http = CreateHttpContext();

        await ScopeWorkflowEndpoints.HandleRunWorkflowStreamAsync(
            http,
            "user-1",
            new ScopeWorkflowEndpoints.RunScopeWorkflowStreamHttpRequest("actor-404", "hello"),
            BuildQueryPort(),
            new FakeCommandInteractionService(),
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("USER_WORKFLOW_NOT_FOUND");
    }

    [Fact]
    public async Task HandleRunWorkflowStreamAsync_ShouldReturnForbidden_WhenAuthenticatedScopeClaimMismatchesPath()
    {
        var http = CreateAuthenticatedHttpContext("user-2");
        var interactionService = new FakeCommandInteractionService();

        await ScopeWorkflowEndpoints.HandleRunWorkflowStreamAsync(
            http,
            "user-1",
            new ScopeWorkflowEndpoints.RunScopeWorkflowStreamHttpRequest("actor-1", "hello"),
            BuildQueryPort(),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        body.Should().Contain("SCOPE_ACCESS_DENIED");
        interactionService.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task HandleRunWorkflowStreamAsync_ShouldReturnForbidden_WhenAuthenticationIsMissing()
    {
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult =
            [
                new ServiceCatalogSnapshot(
                    "tenant-a:workflow-app:user:token:approval",
                    "tenant-a",
                    "workflow-app",
                    "user:user-1-token",
                    "approval",
                    "Approval",
                    "rev-1",
                    "rev-1",
                    "dep-1",
                    "definition-actor-1",
                    "active",
                    [],
                    [],
                    DateTimeOffset.UtcNow),
            ],
        };
        var interactionService = new FakeCommandInteractionService();
        var http = CreateAnonymousHttpContext();

        await ScopeWorkflowEndpoints.HandleRunWorkflowStreamAsync(
            http,
            "user-1",
            new ScopeWorkflowEndpoints.RunScopeWorkflowStreamHttpRequest("definition-actor-1", "hello"),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        body.Should().Contain("SCOPE_ACCESS_DENIED");
        body.Should().Contain("Authentication is required.");
        interactionService.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task HandleGetWorkflowDetailAsync_ShouldPreferWorkflowBindingSource_WhenBindingExists()
    {
        var http = CreateHttpContext();
        var snapshot = new ServiceCatalogSnapshot(
            "tenant-a:workflow-app:user:token:approval",
            "tenant-a",
            "workflow-app",
            "user:user-1-token",
            "approval",
            "Approval",
            "rev-1",
            "rev-1",
            "dep-1",
            "definition-actor-1",
            "active",
            [],
            [],
            DateTimeOffset.UtcNow);
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult = [snapshot],
        };
        queryPort.GetServiceResults.Enqueue(snapshot);
        var bindingReader = new FakeWorkflowActorBindingReader();
        bindingReader.Bindings["definition-actor-1"] = new WorkflowActorBinding(
            WorkflowActorKind.Definition,
            "definition-actor-1",
            "definition-actor-1",
            string.Empty,
            "approval",
            "name: approval\nsteps: []\n",
            new Dictionary<string, string>
            {
                ["child"] = "name: child\nsteps: []\n",
            });
        var artifactStore = new FakeServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            "tenant-a:workflow-app:user:token:approval",
            "rev-1",
            new PreparedServiceRevisionArtifact
            {
                RevisionId = "rev-1",
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "approval",
                        WorkflowYaml = "name: approval\nsteps: []\n",
                        DefinitionActorId = "definition-actor-1",
                    },
                },
            },
            CancellationToken.None);

        var result = await ScopeWorkflowEndpoints.HandleGetWorkflowDetailAsync(
            http,
            "user-1",
            "approval",
            BuildQueryPort(queryPort: queryPort, bindingReader: bindingReader),
            bindingReader,
            artifactStore,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("\"available\":true");
        body.Should().Contain("\"workflowId\":\"approval\"");
        body.Should().Contain("\"workflowYaml\":\"name: approval\\nsteps: []\\n\"");
        body.Should().Contain("\"inlineWorkflowYamls\":{\"child\":\"name: child\\nsteps: []\\n\"}");
    }

    [Fact]
    public async Task HandleListWorkflowsAsync_ShouldIncludeWorkflowSources_WhenRequested()
    {
        var http = CreateHttpContext();
        var snapshot = new ServiceCatalogSnapshot(
            "tenant-a:workflow-app:user:token:approval",
            "tenant-a",
            "workflow-app",
            "user:user-1-token",
            "approval",
            "Approval",
            "rev-1",
            "rev-1",
            "dep-1",
            "definition-actor-1",
            "active",
            [],
            [],
            DateTimeOffset.UtcNow);
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult = [snapshot],
        };
        var bindingReader = new FakeWorkflowActorBindingReader();
        bindingReader.Bindings["definition-actor-1"] = new WorkflowActorBinding(
            WorkflowActorKind.Definition,
            "definition-actor-1",
            "definition-actor-1",
            string.Empty,
            "approval",
            "name: approval\nsteps: []\n",
            new Dictionary<string, string>());

        var result = await ScopeWorkflowEndpoints.HandleListWorkflowsAsync(
            http,
            "user-1",
            includeSource: true,
            BuildQueryPort(queryPort: queryPort, bindingReader: bindingReader),
            bindingReader,
            artifactStore: null,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("\"workflowId\":\"approval\"");
        body.Should().Contain("\"source\":{\"workflowYaml\":\"name: approval\\nsteps: []\\n\"");
    }

    [Fact]
    public async Task HandleRunWorkflowStreamAsync_ShouldDelegateToWorkflowChatPipeline_WhenOwnershipMatches()
    {
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult =
            [
                new ServiceCatalogSnapshot(
                    "tenant-a:workflow-app:user:token:approval",
                    "tenant-a",
                    "workflow-app",
                    "user:user-1-token",
                    "approval",
                    "Approval",
                    "rev-1",
                    "rev-1",
                    "dep-1",
                    "definition-actor-1",
                    "active",
                    [],
                    [],
                    DateTimeOffset.UtcNow),
            ],
        };
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "approval", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        MessageId = "msg-1",
                        Delta = "hello",
                    },
                }, ct);
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext();

        await ScopeWorkflowEndpoints.HandleRunWorkflowStreamAsync(
            http,
            "user-1",
            new ScopeWorkflowEndpoints.RunScopeWorkflowStreamHttpRequest(
                "definition-actor-1",
                "hello",
                "session-1",
                new Dictionary<string, string> { ["source"] = "user-api" }),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("aevatar.run.context");
        body.Should().Contain("\"delta\": \"hello\"");
        interactionService.LastRequest.Should().NotBeNull();
        interactionService.LastRequest!.ActorId.Should().Be("definition-actor-1");
        interactionService.LastRequest.SessionId.Should().Be("session-1");
        interactionService.LastRequest.ScopeId.Should().Be("user-1");
        interactionService.LastRequest.Metadata.Should().ContainKey("source").WhoseValue.Should().Be("user-api");
        interactionService.LastRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        interactionService.LastRequest.Metadata.Should().NotContainKey("scope_id");
    }

    [Fact]
    public async Task HandleRunWorkflowByIdStreamAsync_ShouldStreamAguiEvents_WhenRequested()
    {
        var snapshot = new ServiceCatalogSnapshot(
            "tenant-a:workflow-app:user:token:approval",
            "tenant-a",
            "workflow-app",
            "user:user-1-token",
            "approval",
            "Approval",
            "rev-1",
            "rev-1",
            "dep-1",
            "definition-actor-1",
            "active",
            [],
            [],
            DateTimeOffset.UtcNow);
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult = [snapshot],
        };
        queryPort.GetServiceResults.Enqueue(snapshot);
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("definition-actor-1", "approval", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    StepStarted = new WorkflowStepStartedEventPayload
                    {
                        StepName = "start",
                    },
                }, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        MessageId = "msg-1",
                        Delta = "hello",
                    },
                }, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    Custom = new WorkflowCustomEventPayload
                    {
                        Name = "aevatar.human_input.request",
                        Payload = Any.Pack(new WorkflowHumanInputRequestCustomPayload
                        {
                            StepId = "approve",
                            RunId = "corr-1",
                            Prompt = "Need approval",
                            SuspensionType = "approval",
                            TimeoutSeconds = 30,
                            VariableName = "approval",
                        }),
                    },
                }, ct);
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext();

        await ScopeWorkflowEndpoints.HandleRunWorkflowByIdStreamAsync(
            http,
            "user-1",
            "approval",
            new ScopeWorkflowEndpoints.RunScopeWorkflowByIdStreamHttpRequest(
                "hello",
                Headers: new Dictionary<string, string> { ["scope_id"] = "aevatar" },
                EventFormat: "agui"),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("aevatar.run.context");
        body.Should().Contain("\"stepStarted\": { \"stepName\": \"start\" }");
        body.Should().Contain("\"textMessageContent\": { \"messageId\": \"msg-1\", \"delta\": \"hello\" }");
        body.Should().Contain("\"humanInputRequest\": { \"stepId\": \"approve\"");
        interactionService.LastRequest.Should().NotBeNull();
        interactionService.LastRequest!.ActorId.Should().Be("definition-actor-1");
        interactionService.LastRequest.ScopeId.Should().Be("user-1");
        interactionService.LastRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        interactionService.LastRequest.Metadata.Should().NotContainKey("scope_id");
    }

    [Fact]
    public async Task HandleRunWorkflowByIdStreamAsync_ShouldSerializeRawObservedWorkflowExecutionStartedPayload()
    {
        var snapshot = new ServiceCatalogSnapshot(
            "tenant-a:workflow-app:user:token:approval",
            "tenant-a",
            "workflow-app",
            "user:user-1-token",
            "approval",
            "Approval",
            "rev-1",
            "rev-1",
            "dep-1",
            "definition-actor-1",
            "active",
            [],
            [],
            DateTimeOffset.UtcNow);
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult = [snapshot],
        };
        queryPort.GetServiceResults.Enqueue(snapshot);
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (_, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("definition-actor-1", "approval", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(BuildRawObservedWorkflowExecutionStartedFrame(), ct);
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext();

        await ScopeWorkflowEndpoints.HandleRunWorkflowByIdStreamAsync(
            http,
            "user-1",
            "approval",
            new ScopeWorkflowEndpoints.RunScopeWorkflowByIdStreamHttpRequest(
                "hello",
                Headers: new Dictionary<string, string> { ["scope_id"] = "aevatar" },
                EventFormat: "agui"),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("aevatar.run.context");
        body.Should().Contain("aevatar.raw.observed");
        body.Should().Contain("WorkflowRunExecutionStartedEvent");
        body.Should().Contain("\"runId\": \"run-1\"");
        body.Should().NotContain("EXECUTION_FAILED");
        interactionService.LastRequest.Should().NotBeNull();
        interactionService.LastRequest!.ScopeId.Should().Be("user-1");
        interactionService.LastRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        interactionService.LastRequest.Metadata.Should().NotContainKey("scope_id");
    }

    [Fact]
    public async Task HandleRunWorkflowStreamAsync_ShouldSucceed_WhenScopeClaimMatchesPath()
    {
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult =
            [
                new ServiceCatalogSnapshot(
                    "tenant-a:workflow-app:user:token:approval",
                    "tenant-a",
                    "workflow-app",
                    "user:user-1-token",
                    "approval",
                    "Approval",
                    "rev-1",
                    "rev-1",
                    "dep-1",
                    "definition-actor-1",
                    "active",
                    [],
                    [],
                    DateTimeOffset.UtcNow),
            ],
        };
        var interactionService = new FakeCommandInteractionService
        {
            ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("definition-actor-1", "approval", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);
                await emitAsync(new WorkflowRunEventEnvelope
                {
                    TextMessageContent = new WorkflowTextMessageContentEventPayload
                    {
                        MessageId = "msg-1",
                        Delta = "hi",
                    },
                }, ct);
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateAuthenticatedHttpContext("user-1");

        await ScopeWorkflowEndpoints.HandleRunWorkflowStreamAsync(
            http,
            "user-1",
            new ScopeWorkflowEndpoints.RunScopeWorkflowStreamHttpRequest("definition-actor-1", "hello"),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        http.Response.StatusCode.Should().NotBe(StatusCodes.Status403Forbidden);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandleListWorkflowsAsync_ShouldReturnEmptyArray_WhenNoWorkflows()
    {
        var http = CreateHttpContext();

        var result = await ScopeWorkflowEndpoints.HandleListWorkflowsAsync(
            http,
            "user-1",
            includeSource: false,
            BuildQueryPort(),
            new FakeWorkflowActorBindingReader(),
            artifactStore: null,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Be("[]");
    }

    [Fact]
    public async Task HandleGetWorkflowDetailAsync_ShouldReturnNotFound_WhenWorkflowDoesNotExist()
    {
        var http = CreateHttpContext();

        var result = await ScopeWorkflowEndpoints.HandleGetWorkflowDetailAsync(
            http,
            "user-1",
            "nonexistent-workflow",
            BuildQueryPort(),
            new FakeWorkflowActorBindingReader(),
            artifactStore: null,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("USER_WORKFLOW_NOT_FOUND");
        body.Should().Contain("nonexistent-workflow");
    }

    [Fact]
    public async Task HandleUpsertWorkflowAsync_ShouldReturnOk_WhenCommandSucceeds()
    {
        var http = CreateHttpContext();
        var snapshot = new ServiceCatalogSnapshot(
            "tenant-a:workflow-app:user:token:approval",
            "tenant-a",
            "workflow-app",
            "user:user-1-token",
            "approval",
            "Approval",
            "rev-1",
            "rev-1",
            "dep-1",
            "definition-actor-1",
            "active",
            [],
            [],
            DateTimeOffset.UtcNow);
        var queryPort = new FakeServiceLifecycleQueryPort
        {
            ListServicesResult = [snapshot],
        };
        queryPort.GetServiceResults.Enqueue(snapshot);

        var result = await ScopeWorkflowEndpoints.HandleUpsertWorkflowAsync(
            http,
            "user-1",
            "approval",
            new ScopeWorkflowEndpoints.UpsertScopeWorkflowHttpRequest("name: approval\nsteps: []\n"),
            BuildCommandPort(queryPort: queryPort),
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    private static IScopeWorkflowCommandPort BuildCommandPort(
        FakeServiceCommandPort? commandPort = null,
        FakeServiceLifecycleQueryPort? queryPort = null,
        FakeWorkflowActorBindingReader? bindingReader = null)
    {
        var resolvedQueryPort = queryPort ?? new FakeServiceLifecycleQueryPort();
        var queryService = BuildQueryApplicationService(resolvedQueryPort, bindingReader);
        return new ScopeWorkflowCommandApplicationService(
            commandPort ?? new FakeServiceCommandPort(),
            resolvedQueryPort,
            new NoOpServiceGovernanceCommandPort(),
            new NoOpServiceGovernanceQueryPort(),
            queryService,
            Options.Create(new ScopeWorkflowCapabilityOptions
            {
                ServiceAppId = "default",
                ServiceNamespace = "default",
                DefinitionActorIdPrefix = "scope-workflow",
            }));
    }

    private static IScopeWorkflowQueryPort BuildQueryPort(
        FakeServiceLifecycleQueryPort? queryPort = null,
        FakeWorkflowActorBindingReader? bindingReader = null) =>
        BuildQueryApplicationService(queryPort, bindingReader);

    private static ScopeWorkflowQueryApplicationService BuildQueryApplicationService(
        FakeServiceLifecycleQueryPort? queryPort = null,
        FakeWorkflowActorBindingReader? bindingReader = null)
    {
        return new ScopeWorkflowQueryApplicationService(
            queryPort ?? new FakeServiceLifecycleQueryPort(),
            bindingReader ?? new FakeWorkflowActorBindingReader(),
            Options.Create(new ScopeWorkflowCapabilityOptions
            {
                ServiceAppId = "default",
                ServiceNamespace = "default",
                DefinitionActorIdPrefix = "scope-workflow",
            }));
    }

    private static DefaultHttpContext CreateHttpContext(string scopeId = "user-1")
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        http.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim("scope_id", scopeId)],
                authenticationType: "test"));
        return http;
    }

    private static DefaultHttpContext CreateAuthenticatedHttpContext(string scopeId) => CreateHttpContext(scopeId);

    private static DefaultHttpContext CreateAnonymousHttpContext()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static WorkflowRunEventEnvelope BuildRawObservedWorkflowExecutionStartedFrame()
    {
        var payload = new WorkflowRunExecutionStartedEvent
        {
            RunId = "run-1",
            WorkflowName = "approval",
            Input = "hello",
            DefinitionActorId = "definition-actor-1",
        };

        return new WorkflowRunEventEnvelope
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = "aevatar.raw.observed",
                Payload = Any.Pack(new WorkflowObservedEnvelopeCustomPayload
                {
                    EventId = "evt-1",
                    PayloadTypeUrl = Any.Pack(payload).TypeUrl,
                    PublisherActorId = "definition-actor-1",
                    CorrelationId = "corr-1",
                    StateVersion = 1,
                    Payload = Any.Pack(payload),
                }),
            },
        };
    }

    private sealed class FakeCommandInteractionService
        : ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>
    {
        public WorkflowChatRunRequest? LastRequest { get; private set; }

        public Func<WorkflowChatRunRequest, Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask>, Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>>> ResultFactory { get; set; } =
            (_, _, _, _) => Task.FromResult(
                CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                    .Failure(WorkflowChatRunStartError.AgentNotFound));

        public Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return ResultFactory(request, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class FakeServiceCommandPort : IServiceCommandPort
    {
        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceRolloutCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(AcceptedRollout(ServiceRolloutStatus.Paused));
        public Task<ServiceRolloutCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(AcceptedRollout(ServiceRolloutStatus.InProgress));
        public Task<ServiceRolloutCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(AcceptedRollout(ServiceRolloutStatus.RolledBack));

        private static ServiceCommandAcceptedReceipt Accepted() => new("target-actor", "cmd-1", "corr-1");
        private static ServiceRolloutCommandAcceptedReceipt AcceptedRollout(ServiceRolloutStatus status) => new("target-actor", "cmd-1", "corr-1", false, status.ToString());
    }

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public sealed record ListRequest(string TenantId, string AppId, string Namespace, int Take);

        public readonly Queue<ServiceCatalogSnapshot?> GetServiceResults = new();
        public IReadOnlyList<ServiceCatalogSnapshot> ListServicesResult { get; set; } = [];
        public ServiceIdentity? LastGetIdentity { get; private set; }
        public ListRequest? LastListRequest { get; private set; }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetIdentity = identity;
            return Task.FromResult(GetServiceResults.Count > 0 ? GetServiceResults.Dequeue() : null);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default)
        {
            LastListRequest = new ListRequest(tenantId, appId, @namespace, take);
            return Task.FromResult(ListServicesResult);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);
        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class FakeWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        public Dictionary<string, WorkflowActorBinding> Bindings { get; } = new(StringComparer.Ordinal);

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult(Bindings.TryGetValue(actorId, out var binding) ? binding : null);
    }

    private sealed class FakeServiceRevisionArtifactStore : IServiceRevisionArtifactStore
    {
        private readonly Dictionary<string, PreparedServiceRevisionArtifact> _artifacts = new(StringComparer.Ordinal);

        public Task SaveAsync(string serviceKey, string revisionId, PreparedServiceRevisionArtifact artifact, CancellationToken ct = default)
        {
            _artifacts[$"{serviceKey}:{revisionId}"] = artifact;
            return Task.CompletedTask;
        }

        public Task<PreparedServiceRevisionArtifact?> GetAsync(string serviceKey, string revisionId, CancellationToken ct = default)
        {
            _artifacts.TryGetValue($"{serviceKey}:{revisionId}", out var artifact);
            return Task.FromResult<PreparedServiceRevisionArtifact?>(artifact);
        }
    }

    private sealed class RecordingDispatchService<TCommand, TReceipt, TError>
        : ICommandDispatchService<TCommand, TReceipt, TError>
    {
        public List<TCommand> Commands { get; } = [];

        public CommandDispatchResult<TReceipt, TError> Result { get; set; } =
            CommandDispatchResult<TReceipt, TError>.Failure(default!);

        public Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(TCommand command, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(Result);
        }
    }

    private sealed class NoOpServiceGovernanceCommandPort : IServiceGovernanceCommandPort
    {
        private static readonly ServiceCommandAcceptedReceipt DefaultReceipt =
            new("governance-actor", "cmd-governance", "corr-governance");

        public Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(CreateServiceBindingCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(UpdateServiceBindingCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(RetireServiceBindingCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(CreateServiceEndpointCatalogCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(UpdateServiceEndpointCatalogCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(CreateServicePolicyCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(UpdateServicePolicyCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);

        public Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(RetireServicePolicyCommand command, CancellationToken ct = default) =>
            Task.FromResult(DefaultReceipt);
    }

    private sealed class NoOpServiceGovernanceQueryPort : IServiceGovernanceQueryPort
    {
        public Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceBindingCatalogSnapshot?>(null);

        public Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceEndpointCatalogSnapshot?>(null);

        public Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServicePolicyCatalogSnapshot?>(null);
    }
}
