using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeWorkflowEndpointsTests
{
    [Fact]
    public async Task HandleExplicitRequestPreviewAsync_ShouldReturnSanitizedCanonicalPreview()
    {
        const string bearer = "transient-preview-bearer";
        const string rawRequestSecret = "raw-request-secret";
        var http = CreateHttpContext();
        http.Request.Headers.Authorization = $"Bearer {bearer}";
        var previewService = new RecordingWorkflowExplicitRequestPreviewService
        {
            Result = new WorkflowExplicitRequestPreviewResult(
                "wf-alpha",
                "rev-alpha",
            [
                new WorkflowExplicitRequestPreviewItem(
                    "wf-alpha/request-alpha",
                    "digest-alpha",
                    "usvc-alpha",
                    NyxIdRequestMethod.Post,
                    "/records/{id}",
                    NyxIdRequestBodyMode.Json,
                    true,
                    NyxIdRequestResponseMode.Text,
                    NyxIdOperationRisk.Write,
                    true,
                    [ExternalCapabilityExecutionMode.Interactive]),
            ]),
        };

        var result = await ScopeWorkflowEndpoints.HandleExplicitRequestPreviewAsync(
            http,
            "user-1",
            new ScopeWorkflowEndpoints.ExplicitRequestPreviewHttpRequest(
                $"name: wf-alpha\nsecret: {rawRequestSecret}\n",
                "interactive",
                new Dictionary<string, string>
                {
                    ["child"] = $"name: child\nsecret: {rawRequestSecret}\n",
                },
                WorkflowId: "wf-alpha",
                RevisionId: "rev-alpha"),
            previewService,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        previewService.Request.Should().NotBeNull();
        previewService.Request!.Access.ScopeId.Should().Be("user-1");
        previewService.Request.Access.CallerId.Should().Be("caller-alpha");
        previewService.Request.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be(bearer);
        previewService.Request.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Interactive);
        previewService.Request.WorkflowId.Should().Be("wf-alpha");
        previewService.Request.RevisionId.Should().Be("rev-alpha");
        body.Should().Contain("\"callSiteId\":\"wf-alpha/request-alpha\"");
        body.Should().Contain("\"workflowId\":\"wf-alpha\"");
        body.Should().Contain("\"revisionId\":\"rev-alpha\"");
        body.Should().Contain("\"requestContractDigest\":\"digest-alpha\"");
        body.Should().Contain("\"userServiceId\":\"usvc-alpha\"");
        body.Should().Contain("\"method\":\"post\"");
        body.Should().Contain("\"bodyMode\":\"json\"");
        body.Should().Contain("\"responseMode\":\"text\"");
        body.Should().Contain("\"effectiveRisk\":\"write\"");
        body.Should().Contain("\"allowedExecutionModes\":[\"interactive\"]");
        body.Should().NotContain(bearer);
        body.Should().NotContain(rawRequestSecret);
        body.ToLowerInvariant().Should().NotContain("endpointid");
        body.ToLowerInvariant().Should().NotContain("grant");
    }

    [Theory]
    [InlineData("")]
    [InlineData("background")]
    public async Task HandleExplicitRequestPreviewAsync_ShouldRejectInvalidExecutionModeBeforePreview(
        string executionMode)
    {
        var http = CreateHttpContext();
        var previewService = new RecordingWorkflowExplicitRequestPreviewService();

        var result = await ScopeWorkflowEndpoints.HandleExplicitRequestPreviewAsync(
            http,
            "user-1",
            new ScopeWorkflowEndpoints.ExplicitRequestPreviewHttpRequest(
                "name: wf-alpha\nsteps: []\n",
                executionMode),
            previewService,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_USER_WORKFLOW_REQUEST");
        previewService.Request.Should().BeNull();
    }

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
    public async Task HandleSaveAndBindWorkflowAsync_ShouldReturnAccepted_WithoutRequestRevisionId()
    {
        var http = CreateHttpContext();
        http.Request.Headers.Authorization = "Bearer transient-caller-token";
        var port = new RecordingScopeWorkflowSaveAndBindPort();

        var result = await ScopeWorkflowEndpoints.HandleSaveAndBindWorkflowAsync(
            http,
            "user-1",
            new ScopeWorkflowEndpoints.SaveAndBindScopeWorkflowHttpRequest(
                "wf-alpha",
                "name: approval\nsteps: []\n",
                WorkflowName: "approval",
                DisplayName: "Approval",
                InlineWorkflowYamls: new Dictionary<string, string>
                {
                    ["child"] = "name: child\nsteps: []\n",
                },
                AppId: "studio",
                ServiceId: "svc-alpha",
                ExposureDesired: true,
                ExplicitRequestConfirmations:
                [
                    new NyxIdExplicitRequestConfirmationInput(
                        "wf-alpha/request-alpha",
                        "digest-alpha",
                        "write"),
                ]),
            port,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be("/api/scopes/user-1/workflows/wf-alpha");
        port.Request.Should().NotBeNull();
        port.Request!.ScopeId.Should().Be("user-1");
        port.Request.WorkflowId.Should().Be("wf-alpha");
        port.Request.AppId.Should().Be("studio");
        port.Request.ServiceId.Should().Be("svc-alpha");
        port.Request.ExposureDesired.Should().BeTrue();
        port.Request.CapabilityAdmission.Should().NotBeNull();
        port.Request.CapabilityAdmission!.CallerId.Should().Be("caller-alpha");
        port.Request.CapabilityAdmission.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("transient-caller-token");
        var confirmation = port.Request.CapabilityAdmission.ExplicitRequestConfirmations
            .Should().ContainSingle().Which;
        confirmation.CallSiteId.Should().Be("wf-alpha/request-alpha");
        confirmation.RequestContractDigest.Should().Be("digest-alpha");
        confirmation.AttestedRisk.Should().Be(NyxIdOperationRisk.Write);
        body.Should().Contain("\"revisionId\":\"rev-generated\"");
        body.Should().Contain("\"acceptanceStage\":\"accepted\"");
        body.Should().Contain("\"propagationStage\":\"readmodel_propagating\"");
    }

    [Fact]
    public async Task HandleUpsertWorkflowAsync_ShouldMapExplicitRequestConfirmationFromHttpInput()
    {
        var http = CreateHttpContext();
        http.Request.Headers.Authorization = "Bearer transient-upsert-token";
        var port = new RecordingScopeWorkflowCommandPort();

        var result = await ScopeWorkflowEndpoints.HandleUpsertWorkflowAsync(
            http,
            "user-1",
            "wf-alpha",
            new ScopeWorkflowEndpoints.UpsertScopeWorkflowHttpRequest(
                "name: wf-alpha\nsteps: []\n",
                RevisionId: "rev-alpha",
                ExplicitRequestConfirmations:
                [
                    new NyxIdExplicitRequestConfirmationInput(
                        "wf-alpha/request-alpha",
                        "digest-alpha",
                        "destructive"),
                ]),
            port,
            CancellationToken.None);

        await result.ExecuteAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        port.Request.Should().NotBeNull();
        port.Request!.WorkflowId.Should().Be("wf-alpha");
        port.Request.RevisionId.Should().Be("rev-alpha");
        port.Request.CapabilityAdmission.Should().NotBeNull();
        port.Request.CapabilityAdmission!.CallerId.Should().Be("caller-alpha");
        port.Request.CapabilityAdmission.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("transient-upsert-token");
        var confirmation = port.Request.CapabilityAdmission.ExplicitRequestConfirmations
            .Should().ContainSingle().Which;
        confirmation.CallSiteId.Should().Be("wf-alpha/request-alpha");
        confirmation.RequestContractDigest.Should().Be("digest-alpha");
        confirmation.AttestedRisk.Should().Be(NyxIdOperationRisk.Destructive);
    }

    [Fact]
    public async Task WorkflowWriteEndpoints_WithNullExplicitRequestConfirmation_ShouldReturnTypedBadRequestWithoutDispatch()
    {
        var upsertHttp = CreateHttpContext();
        var upsertPort = new RecordingScopeWorkflowCommandPort();
        var upsertResult = await ScopeWorkflowEndpoints.HandleUpsertWorkflowAsync(
            upsertHttp,
            "user-1",
            "wf-alpha",
            new ScopeWorkflowEndpoints.UpsertScopeWorkflowHttpRequest(
                "name: wf-alpha\nsteps: []\n",
                RevisionId: "rev-alpha",
                ExplicitRequestConfirmations: [null!]),
            upsertPort,
            CancellationToken.None);

        await upsertResult.ExecuteAsync(upsertHttp);
        var upsertBody = await ReadBodyAsync(upsertHttp.Response);

        upsertHttp.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        upsertBody.Should().Contain("INVALID_EXPLICIT_REQUEST_CONFIRMATION");
        upsertPort.Request.Should().BeNull();

        var saveAndBindHttp = CreateHttpContext();
        var saveAndBindPort = new RecordingScopeWorkflowSaveAndBindPort();
        var saveAndBindResult = await ScopeWorkflowEndpoints.HandleSaveAndBindWorkflowAsync(
            saveAndBindHttp,
            "user-1",
            new ScopeWorkflowEndpoints.SaveAndBindScopeWorkflowHttpRequest(
                "wf-alpha",
                "name: wf-alpha\nsteps: []\n",
                ExplicitRequestConfirmations: [null!]),
            saveAndBindPort,
            CancellationToken.None);

        await saveAndBindResult.ExecuteAsync(saveAndBindHttp);
        var saveAndBindBody = await ReadBodyAsync(saveAndBindHttp.Response);

        saveAndBindHttp.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        saveAndBindBody.Should().Contain("INVALID_EXPLICIT_REQUEST_CONFIRMATION");
        saveAndBindPort.Request.Should().BeNull();
    }

    [Theory]
    [InlineData("malformed_with_delegation")]
    [InlineData("multiple_authorization_values")]
    public async Task HandleUpsertWorkflowAsync_WithInvalidAuthorization_ShouldReturnTypedBadRequestWithoutDispatch(
        string scenario)
    {
        var http = CreateHttpContext();
        if (scenario == "malformed_with_delegation")
        {
            http.Request.Headers.Authorization = "Bearer token with spaces";
            http.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";
        }
        else
        {
            http.Request.Headers.Authorization =
                new Microsoft.Extensions.Primitives.StringValues(["Bearer first", "Bearer second"]);
        }
        var port = new RecordingScopeWorkflowCommandPort();

        var result = await ScopeWorkflowEndpoints.HandleUpsertWorkflowAsync(
            http,
            "user-1",
            "wf-alpha",
            new ScopeWorkflowEndpoints.UpsertScopeWorkflowHttpRequest(
                "name: wf-alpha\nsteps: []\n",
                RevisionId: "rev-alpha"),
            port,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_WORKFLOW_CALLER_CREDENTIAL");
        body.Should().NotContain("token with spaces");
        body.Should().NotContain("delegation-token");
        port.Request.Should().BeNull();
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
            },
            ExternalCapabilityExecutionMode.Durable);
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
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
            revisionCatalog,
            Options.Create(new ScopeWorkflowCapabilityOptions()),
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
            new Dictionary<string, string>(),
            ExternalCapabilityExecutionMode.Durable);

        var result = await ScopeWorkflowEndpoints.HandleListWorkflowsAsync(
            http,
            "user-1",
            includeSource: true,
            BuildQueryPort(queryPort: queryPort, bindingReader: bindingReader),
            bindingReader,
            new FakeServiceRevisionCatalogQueryReader(),
            Options.Create(new ScopeWorkflowCapabilityOptions()),
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
                return WorkflowChatRunInteractionResult
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
        interactionService.LastRequest!.Source.ActorId.Should().Be("definition-actor-1");
        interactionService.LastRequest.SessionId.Should().Be("session-1");
        interactionService.LastRequest.ScopeId.Should().Be("user-1");
        interactionService.LastRequest.Metadata.Should().BeNullOrEmpty();
        interactionService.LastRequest.Headers.Should().ContainKey("source").WhoseValue.Should().Be("user-api");
        interactionService.LastRequest.Headers.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        interactionService.LastRequest.Headers.Should().NotContainKey("scope_id");
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
                return WorkflowChatRunInteractionResult
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext();
        http.Request.Headers.Authorization = "Bearer token-123";

        var scopedControlInput = await ScopeWorkflowEndpoints.BuildScopedLlmControlInputAsync(
            http,
            CancellationToken.None);
        scopedControlInput.Should().BeNull();

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
        interactionService.LastRequest!.Source.ActorId.Should().Be("definition-actor-1");
        interactionService.LastRequest.ScopeId.Should().Be("user-1");
        interactionService.LastRequest.CallerCredential!.BearerToken.Should().Be("token-123");
        interactionService.LastRequest.LlmControl.Should().BeNull();
        interactionService.LastRequest.Metadata.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        interactionService.LastRequest.Metadata.Should().NotContainKey("scope_id");
        interactionService.LastRequest.Metadata.Should().NotContainKey("connector.http.authorization");
        interactionService.LastRequest.Headers.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        interactionService.LastRequest.Headers.Should().NotContainKey("scope_id");
    }

    [Fact]
    public async Task BuildScopedLlmControlInputAsync_ShouldFallBackToProviderDefaults_WhenUserConfigFails()
    {
        var http = CreateHttpContext(userConfigQueryPort: new ThrowingUserConfigStore());

        var scopedControlInput = await ScopeWorkflowEndpoints.BuildScopedLlmControlInputAsync(
            http,
            CancellationToken.None);

        scopedControlInput.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildScopedLlmControlInputAsync_WithoutTypedSelection_ShouldIgnoreCompatibilityRoute(
        bool useUnspecifiedSelection)
    {
        const string prefixedModel = "chrono-llm/gpt-5.5";
        var selection = useUnspecifiedSelection
            ? new LLMSelection
            {
                RouteKind = LLMRouteKind.Unspecified,
                RouteValue = "/api/v1/proxy/s/typed-but-ignored",
                NyxIdUserServiceId = "us-ignored",
                ServiceSlugSnapshot = "ignored",
                ModelSelection = new LLMModelSelection { Kind = LLMModelSelectionKind.Unspecified },
            }
            : null;
        var http = CreateHttpContext(
            userConfigQueryPort: new StubUserConfigStore(new UserConfig(
                DefaultModel: prefixedModel,
                PreferredLlmRoute: "/api/v1/proxy/s/legacy",
                LlmSelection: selection)));

        var control = await ScopeWorkflowEndpoints.BuildScopedLlmControlInputAsync(
            http,
            CancellationToken.None);

        control.Should().NotBeNull();
        control!.ModelOverride.Should().Be(prefixedModel);
        control.NyxIdRoutePreference.Should().BeNull();
    }

    [Fact]
    public async Task BuildScopedLlmControlInputAsync_WithTypedGateway_ShouldUseCanonicalGateway()
    {
        var http = CreateHttpContext(
            userConfigQueryPort: new StubUserConfigStore(new UserConfig(
                DefaultModel: "gpt-5.5",
                PreferredLlmRoute: "/api/v1/proxy/s/legacy",
                LlmSelection: new LLMSelection
                {
                    RouteKind = LLMRouteKind.Gateway,
                    RouteValue = "/api/v1/proxy/s/typed-but-ignored",
                    NyxIdUserServiceId = "us-ignored",
                    ServiceSlugSnapshot = "ignored",
                    ModelSelection = new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault },
                })));

        var control = await ScopeWorkflowEndpoints.BuildScopedLlmControlInputAsync(
            http,
            CancellationToken.None);

        control.Should().NotBeNull();
        control!.NyxIdRoutePreference.Should().Be(UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task HandleRunWorkflowByIdStreamAsync_ShouldReturnNotReady_WhenRunnableReadmodelIsMissing()
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
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    [],
                    [],
                    DateTimeOffset.UtcNow),
            ],
        };
        queryPort.GetServiceResults.Enqueue(queryPort.ListServicesResult[0]);
        var interactionService = new FakeCommandInteractionService();
        var http = CreateHttpContext();

        await ScopeWorkflowEndpoints.HandleRunWorkflowByIdStreamAsync(
            http,
            "user-1",
            "approval",
            new ScopeWorkflowEndpoints.RunScopeWorkflowByIdStreamHttpRequest(
                "hello",
                EventFormat: "agui"),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.Should().Contain("USER_WORKFLOW_NOT_READY");
        interactionService.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task HandleRunWorkflowByIdStreamAsync_ShouldPropagateScopedPreferredLlmRouteToAguiRequest()
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
            ResultFactory = async (_, _, onAcceptedAsync, ct) =>
            {
                var receipt = new WorkflowChatRunAcceptedReceipt("definition-actor-1", "approval", "cmd-1", "corr-1");
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct);

                return WorkflowChatRunInteractionResult
                    .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
            },
        };
        var http = CreateHttpContext(
            userConfigQueryPort: new StubUserConfigStore(
                new UserConfig(
                    DefaultModel: string.Empty,
                    PreferredLlmRoute: "/api/v1/proxy/s/legacy",
                    LlmSelection: new LLMSelection
                    {
                        RouteKind = LLMRouteKind.NyxIdUserService,
                        RouteValue = " /preferred-route ",
                        NyxIdUserServiceId = "us-preferred",
                        ServiceSlugSnapshot = "preferred",
                        ModelSelection = new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault },
                    })));

        await ScopeWorkflowEndpoints.HandleRunWorkflowByIdStreamAsync(
            http,
            "user-1",
            "approval",
            new ScopeWorkflowEndpoints.RunScopeWorkflowByIdStreamHttpRequest(
                "hello",
                EventFormat: "agui"),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        interactionService.LastRequest.Should().NotBeNull();
        interactionService.LastRequest!.LlmControl.Should().NotBeNull();
        interactionService.LastRequest.LlmControl!.RoutePreference.Should().Be("/preferred-route");
        interactionService.LastRequest.LlmControl.ModelOverride.Should().BeNull();
    }

    [Fact]
    public async Task HandleRunWorkflowByIdStreamAsync_ShouldReturnInvalidCallerCredential_WhenBearerIsMalformed()
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
        var interactionService = new FakeCommandInteractionService();
        var http = CreateHttpContext();
        http.Request.Headers.Authorization = "Bearer token 123";

        await ScopeWorkflowEndpoints.HandleRunWorkflowByIdStreamAsync(
            http,
            "user-1",
            "approval",
            new ScopeWorkflowEndpoints.RunScopeWorkflowByIdStreamHttpRequest(
                "hello",
                EventFormat: "agui"),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_CALLER_CREDENTIAL");
        body.Should().Contain("Caller credential is invalid.");
        interactionService.LastRequest.Should().BeNull();
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
                return WorkflowChatRunInteractionResult
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
        interactionService.LastRequest.Metadata.Should().BeNullOrEmpty();
        interactionService.LastRequest.Headers.Should().NotContainKey(WorkflowRunCommandMetadataKeys.ScopeId);
        interactionService.LastRequest.Headers.Should().NotContainKey("scope_id");
    }

    [Fact]
    public async Task HandleRunWorkflowByIdStreamAsync_ShouldPreserveMappedMessage_WhenAguiFailsBeforeStart()
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
            ResultFactory = (_, _, _, _) => Task.FromResult(
                WorkflowChatRunInteractionResult
                    .Failure(WorkflowChatRunStartError.WorkflowNotFound)),
        };
        var http = CreateHttpContext();

        await ScopeWorkflowEndpoints.HandleRunWorkflowByIdStreamAsync(
            http,
            "user-1",
            "approval",
            new ScopeWorkflowEndpoints.RunScopeWorkflowByIdStreamHttpRequest(
                "hello",
                EventFormat: "agui"),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("WORKFLOW_NOT_FOUND");
        body.Should().Contain("Workflow not found.");
        body.Should().NotContain("current scope catalog");
    }

    [Fact]
    public async Task HandleRunWorkflowByIdStreamAsync_ShouldReturnServiceUnavailable_WhenProjectionUnavailableBeforeAguiStarts()
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
            ResultFactory = (_, _, _, _) => Task.FromResult(
                WorkflowChatRunInteractionResult
                    .Failure(WorkflowChatRunStartError.ProjectionUnavailable)),
        };
        var http = CreateHttpContext();

        await ScopeWorkflowEndpoints.HandleRunWorkflowByIdStreamAsync(
            http,
            "user-1",
            "approval",
            new ScopeWorkflowEndpoints.RunScopeWorkflowByIdStreamHttpRequest(
                "hello",
                EventFormat: "agui"),
            BuildQueryPort(queryPort: queryPort),
            interactionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("WORKFLOW_PROJECTION_UNAVAILABLE");
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
                return WorkflowChatRunInteractionResult
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
            new FakeServiceRevisionCatalogQueryReader(),
            Options.Create(new ScopeWorkflowCapabilityOptions()),
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
            new FakeServiceRevisionCatalogQueryReader(),
            Options.Create(new ScopeWorkflowCapabilityOptions()),
            CancellationToken.None);

        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("USER_WORKFLOW_NOT_FOUND");
        body.Should().Contain("nonexistent-workflow");
    }

    [Fact]
    public async Task HandleUpsertWorkflowAsync_ShouldReturnAccepted_WithLocation_WhenCommandSucceeds()
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
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        http.Response.Headers.Location.ToString().Should().Be("/api/scopes/user-1/workflows/approval");
        body.Should().Contain("\"acceptanceStage\":\"accepted\"");
        body.Should().Contain("\"propagationStage\":\"readmodel_propagating\"");
        body.Should().Contain("\"readModelUrl\":\"/api/scopes/user-1/workflows/approval\"");
        body.Should().Contain("\"commandHandles\"");
        body.Should().NotContain("\"workflow\"");
    }

    private static IScopeWorkflowCommandPort BuildCommandPort(
        FakeServiceCommandPort? commandPort = null,
        FakeServiceLifecycleQueryPort? queryPort = null,
        FakeWorkflowActorBindingReader? bindingReader = null)
    {
        var resolvedQueryPort = queryPort ?? new FakeServiceLifecycleQueryPort();
        return new ScopeWorkflowCommandApplicationService(
            commandPort ?? new FakeServiceCommandPort(),
            resolvedQueryPort,
            new NoOpServiceGovernanceCommandPort(),
            new NoOpServiceGovernanceQueryPort(),
            Options.Create(new ScopeWorkflowCapabilityOptions
            {
                ServiceAppId = "default",
                ServiceNamespace = "default",
                DefinitionActorIdPrefix = "scope-workflow",
            }),
            new PassthroughWorkflowCapabilityAdmissionService());
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

    private static DefaultHttpContext CreateHttpContext(
        string scopeId = "user-1",
        IUserConfigQueryPort? userConfigQueryPort = null)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = BuildRequestServices(userConfigQueryPort),
        };
        http.Response.Body = new MemoryStream();
        http.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim("scope_id", scopeId),
                    new Claim("sub", "caller-alpha"),
                ],
                authenticationType: "test"));
        return http;
    }

    private static DefaultHttpContext CreateAuthenticatedHttpContext(string scopeId) => CreateHttpContext(scopeId);

    private static DefaultHttpContext CreateAnonymousHttpContext()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = BuildRequestServices(),
        };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static ServiceProvider BuildRequestServices(IUserConfigQueryPort? userConfigQueryPort = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        if (userConfigQueryPort != null)
            services.AddSingleton(userConfigQueryPort);

        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.GAgentService.Integration.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
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

    private sealed class FakeCommandInteractionService : IWorkflowChatRunInteractionPort
    {
        public WorkflowChatRunRequest? LastRequest { get; private set; }

        public Func<WorkflowChatRunRequest, Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask>, Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>?, CancellationToken, Task<WorkflowChatRunInteractionResult>> ResultFactory { get; set; } =
            (_, _, _, _) => Task.FromResult(
                WorkflowChatRunInteractionResult
                    .Failure(WorkflowChatRunStartError.AgentNotFound));

        public Task<WorkflowChatRunInteractionResult> ExecuteAsync(
            WorkflowChatRunRequest request,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatInteractionAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return ResultFactory(request, emitAsync, onAcceptedAsync, ct);
        }
    }

    private sealed class StubUserConfigStore(UserConfig config) : IUserConfigQueryPort
    {
        public Task<UserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(config);

        public Task<UserConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default) => GetAsync(ct);
    }

    private sealed class ThrowingUserConfigStore : IUserConfigQueryPort
    {
        public Task<UserConfig> GetAsync(CancellationToken ct = default) => throw new HttpRequestException("config backend unavailable");

        public Task<UserConfig> GetAsync(UserConfigResourceKey resource, CancellationToken ct = default) => GetAsync(ct);
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
        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());
        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) => Task.FromResult(Accepted());

        private static ServiceCommandAcceptedReceipt Accepted() => new("target-actor", "cmd-1", "corr-1");
    }

    private sealed class FakeServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public sealed record ListRequest(string TenantId, string AppId, string Namespace, int Take);

        public readonly Queue<ServiceCatalogSnapshot?> GetServiceResults = new();
        public IReadOnlyList<ServiceCatalogSnapshot> ListServicesResult { get; set; } = [];
        public ServiceDeploymentCatalogSnapshot? DeploymentResult { get; set; }
        public ServiceIdentity? LastGetIdentity { get; private set; }
        public ListRequest? LastListRequest { get; private set; }
        private ServiceCatalogSnapshot? _lastServiceSnapshot;

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetIdentity = identity;
            _lastServiceSnapshot = GetServiceResults.Count > 0
                ? GetServiceResults.Dequeue()
                : ListServicesResult.FirstOrDefault(x => string.Equals(x.ServiceId, identity.ServiceId, StringComparison.Ordinal));
            return Task.FromResult(_lastServiceSnapshot);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default)
        {
            LastListRequest = new ListRequest(tenantId, appId, @namespace, take);
            return Task.FromResult(ListServicesResult);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            if (DeploymentResult != null)
                return Task.FromResult<ServiceDeploymentCatalogSnapshot?>(DeploymentResult);

            var serviceKey = ServiceKeys.Build(identity);
            var service = ListServicesResult.FirstOrDefault(x => string.Equals(x.ServiceKey, serviceKey, StringComparison.Ordinal))
                ?? _lastServiceSnapshot;
            if (service == null || string.IsNullOrWhiteSpace(service.DeploymentId))
                return Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);

            return Task.FromResult<ServiceDeploymentCatalogSnapshot?>(new ServiceDeploymentCatalogSnapshot(
                service.ServiceKey,
                [new ServiceDeploymentSnapshot(
                    service.DeploymentId,
                    service.ActiveServingRevisionId,
                    service.PrimaryActorId,
                    service.DeploymentStatus,
                    service.UpdatedAt,
                    service.UpdatedAt)],
                service.UpdatedAt));
        }
    }

    private sealed class FakeWorkflowActorBindingReader : IWorkflowActorBindingReader
    {
        public Dictionary<string, WorkflowActorBinding> Bindings { get; } = new(StringComparer.Ordinal);

        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default) =>
            Task.FromResult(Bindings.TryGetValue(actorId, out var binding)
                ? binding
                : new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    actorId,
                    actorId,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    new Dictionary<string, string>(),
                    ExternalCapabilityExecutionMode.Durable));
    }

    private sealed class FakeServiceRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
    {
        private readonly Dictionary<string, PreparedServiceRevisionArtifact> _revisionCatalog = new(StringComparer.Ordinal);

        public Task UpsertRevisionAsync(string serviceKey, string revisionId, PreparedServiceRevisionArtifact artifact, CancellationToken ct = default)
        {
            var clone = artifact.Clone();
            clone.RevisionId = revisionId;
            _revisionCatalog[$"{serviceKey}:{revisionId}"] = clone;
            return Task.CompletedTask;
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            var serviceKey = ServiceKeys.Build(identity);
            var revisions = _revisionCatalog
                .Where(x => x.Key.StartsWith(serviceKey + ":", StringComparison.Ordinal))
                .Select(x => x.Value)
                .Select(artifact => new ServiceRevisionSnapshot(
                    artifact.RevisionId,
                    artifact.ImplementationKind.ToString(),
                    ServiceRevisionStatus.Prepared.ToString(),
                    artifact.ArtifactHash,
                    string.Empty,
                    artifact.Endpoints.Select(endpoint => new ServiceEndpointSnapshot(
                        endpoint.EndpointId,
                        endpoint.DisplayName,
                        endpoint.Kind.ToString(),
                        endpoint.RequestTypeUrl,
                        endpoint.ResponseTypeUrl,
                        endpoint.Description)).ToList(),
                    null,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null,
                    artifact.Clone()))
                .ToList();

            return Task.FromResult<ServiceRevisionCatalogSnapshot?>(new ServiceRevisionCatalogSnapshot(
                serviceKey,
                revisions,
                DateTimeOffset.UtcNow,
                revisions.Count,
                string.Empty));
        }
    }

    private sealed class RecordingScopeWorkflowSaveAndBindPort : IScopeWorkflowSaveAndBindPort
    {
        public ScopeWorkflowSaveAndBindRequest? Request { get; private set; }

        public Task<ScopeWorkflowSaveAndBindResult> SaveAndBindAsync(
            ScopeWorkflowSaveAndBindRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            var workflowId = request.WorkflowId ?? "wf-generated";
            var workflowResult = new ScopeWorkflowUpsertResult(
                request.ScopeId,
                workflowId,
                "scope-service-key",
                "rev-generated",
                "definition-prefix",
                "workflow-actor",
                "deployment-id",
                DateTimeOffset.UtcNow,
                [],
                $"/api/scopes/{request.ScopeId}/workflows/{workflowId}");
            var bindingResult = new ScopeBindingUpsertResult(
                request.ScopeId,
                request.ServiceId ?? "default",
                request.DisplayName ?? "main",
                "rev-generated",
                ScopeBindingImplementationKind.Workflow,
                "binding-actor");
            return Task.FromResult(new ScopeWorkflowSaveAndBindResult(
                request.ScopeId,
                workflowId,
                "rev-generated",
                workflowResult,
                bindingResult));
        }
    }

    private sealed class RecordingScopeWorkflowCommandPort : IScopeWorkflowCommandPort
    {
        public ScopeWorkflowUpsertRequest? Request { get; private set; }

        public Task<ScopeWorkflowUpsertResult> UpsertAsync(
            ScopeWorkflowUpsertRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new ScopeWorkflowUpsertResult(
                request.ScopeId,
                request.WorkflowId,
                "scope-service-key",
                request.RevisionId ?? "rev-generated",
                "definition-prefix",
                "workflow-actor",
                "deployment-id",
                DateTimeOffset.UtcNow,
                [],
                $"/api/scopes/{request.ScopeId}/workflows/{request.WorkflowId}"));
        }
    }

    private sealed class RecordingWorkflowExplicitRequestPreviewService :
        IWorkflowExplicitRequestPreviewService
    {
        public WorkflowExplicitRequestPreviewRequest? Request { get; private set; }

        public WorkflowExplicitRequestPreviewResult Result { get; init; } =
            new WorkflowExplicitRequestPreviewResult("wf-alpha", "rev-alpha", []);

        public Task<WorkflowExplicitRequestPreviewResult> PreviewAsync(
            WorkflowExplicitRequestPreviewRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(Result);
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

    private sealed class PassthroughWorkflowCapabilityAdmissionService : IWorkflowExternalCapabilityAdmissionService
    {
        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WorkflowCapabilityAdmissionPlanIntegrity.Create(
                request.WorkflowYaml,
                request.InlineWorkflowYamls,
                request.ExecutionMode,
                [],
                []));

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Plan.Clone());
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
