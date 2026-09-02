using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.AGUI.Contracts;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

[Collection(ScopeServiceEndpointCollection.Name)]
public sealed class ScopeServiceStreamInvocationEndpointTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task ScopeInvokeStreamEndpoint_ShouldResolveDefaultServiceAndDelegateToWorkflowPipeline()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var service = BuildService("scope-a", "default", "definition-actor-1");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-1",
                            "rev-1",
                            "definition-actor-1",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "default",
                },
                RevisionId = "rev-1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                        ResponseTypeUrl = Any.Pack(new ChatResponseEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "main",
                        WorkflowYaml = "name: main\nsteps:\n  - run: echo hello",
                        DefinitionActorId = "definition-actor-1",
                    },
                },
            },
            CancellationToken.None);
        host.InteractionService.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "main", "cmd-1", "corr-1");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);
            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/invoke/chat:stream")
        {
            Content = JsonContent.Create(new
            {
                prompt = "hello",
                headers = new Dictionary<string, string>
                {
                    ["source"] = "tests",
                    ["connector.http.authorization"] = "Bearer stale-metadata-token",
                },
            }),
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-123");
        var response = await host.Client.SendAsync(httpRequest);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stream body: {0}", body);
        body.Should().Contain("aevatar.run.context");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.Source.ActorId.Should().Be("definition-actor-1");
        host.InteractionService.LastRequest.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.CallerCredential!.BearerToken.Should().Be("token-123");
        host.InteractionService.LastRequest.Metadata.Should().ContainKey("source").WhoseValue.Should().Be("tests");
        host.InteractionService.LastRequest.Metadata.Should().NotContainKey("connector.http.authorization");
        host.InteractionService.LastRequest.Headers.Should().ContainKey("source").WhoseValue.Should().Be("tests");
        // Service-run registry receives the actual workflow run actor id as the run id, so
        // /runs/{runId} can resolve the same id the SSE RunStarted frame carries.
        host.ServiceRunRegistrationPort.RegisterCalls.Should().ContainSingle();
        host.ServiceRunRegistrationPort.RegisterCalls[0].RunId.Should().Be("run-actor-1");
        host.ServiceRunRegistrationPort.RegisterCalls[0].CommandId.Should().Be("cmd-1");
        host.ServiceRunRegistrationPort.RegisterCalls[0].TargetActorId.Should().Be("run-actor-1");
        host.ServiceRunRegistrationPort.RegisterCalls[0].ImplementationKind.Should().Be(ServiceImplementationKind.Workflow);
    }

    [Fact]
    public async Task ScopeInvokeStreamEndpoint_ShouldReturnInvalidCallerCredential_WhenBearerIsMalformed()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/invoke/chat:stream")
        {
            Content = JsonContent.Create(new
            {
                prompt = "hello",
            }),
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token 123");

        var response = await host.Client.SendAsync(httpRequest);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_CALLER_CREDENTIAL");
        body["message"].Should().Be("Caller credential is invalid.");
        host.ServiceCatalogReader.Service.Should().BeNull();
        host.InteractionService.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScopeInvokeDefaultChatStreamEndpoint_ShouldReturnBadRequest_WhenDefaultServiceIsUnbound()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/invoke/chat:stream", new
        {
            prompt = "hello",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SERVICE_STREAM_REQUEST");
        body["message"].Should().Contain("Service 'scope-a:default:default:default' was not found.");
        host.InteractionService.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScopeInvokeStreamEndpoint_ShouldReturnBadRequest_WhenStaticActorTypeCannotBeResolved()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var service = BuildService("scope-a", "default", "definition-actor-1");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-1",
                            "rev-1",
                            "definition-actor-1",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "default",
                },
                RevisionId = "rev-1",
                ImplementationKind = ServiceImplementationKind.Static,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                        ResponseTypeUrl = Any.Pack(new ChatResponseEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    StaticPlan = new StaticServiceDeploymentPlan
                    {
                        ActorTypeName = "Missing.StaticAgent, Missing.Assembly",
                    },
                },
            },
            CancellationToken.None);

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/invoke/chat:stream", new
        {
            prompt = "hello",
        });
        var bodyText = await response.Content.ReadAsStringAsync();
        Dictionary<string, string>? body = null;
        if (!string.IsNullOrWhiteSpace(bodyText) &&
            bodyText.TrimStart().StartsWith('{'))
        {
            body = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(bodyText);
        }

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "stream body: {0}", bodyText);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SERVICE_STREAM_REQUEST");
        body["message"].Should().Contain("could not be resolved");
    }

    [Fact]
    public async Task ScopeInvokeStreamEndpoint_ShouldDelegateStaticServiceToInvocationPort_AndEmitAguiFrames()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var service = BuildService("scope-a", "default", "definition-actor-1");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-1",
                            "rev-1",
                            "definition-actor-1",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "default",
                },
                RevisionId = "rev-1",
                ImplementationKind = ServiceImplementationKind.Static,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                        ResponseTypeUrl = Any.Pack(new ChatResponseEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    StaticPlan = new StaticServiceDeploymentPlan
                    {
                        ActorTypeName = "Test.StaticAgent, Tests",
                    },
                },
            },
            CancellationToken.None);
        host.StaticGAgentStreamInvocationPort.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new StaticGAgentStreamAcceptedReceipt(
                new ServiceInvocationAcceptedReceipt
                {
                    ServiceKey = service.ServiceKey,
                    DeploymentId = "dep-1",
                    TargetActorId = "actor-static-1",
                    EndpointId = request.EndpointId,
                    CommandId = "cmd-static-1",
                    CorrelationId = "corr-static-1",
                },
                new GAgentDraftRunAcceptedReceipt("actor-static-1", "TestStaticGAgent", "cmd-static-1", "corr-static-1"));

            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            await emitAsync(
                new AGUIEvent
                {
                    TextMessageContent = new Aevatar.AGUI.Contracts.TextMessageContentEvent
                    {
                        MessageId = "msg-1",
                        Delta = "hello from static",
                    },
                },
                ct);

            return new StaticGAgentStreamInvocationResult(
                receipt,
                GAgentDraftRunStartError.None,
                GAgentDraftRunCompletionStatus.RunFinished,
                CompletionObserved: true);
        };

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/invoke/chat:stream", new
        {
            prompt = " hello static ",
            actorId = " actor-static-1 ",
            sessionId = "session-1",
            revisionId = "rev-1",
            headers = new Dictionary<string, string> { ["source"] = "tests" },
            inputParts = new[]
            {
                new
                {
                    type = "text",
                    text = (string?)"attachment text",
                    dataBase64 = (string?)null,
                    mediaType = (string?)null,
                    name = (string?)null,
                },
                new
                {
                    type = "image",
                    text = (string?)null,
                    dataBase64 = (string?)"aW1hZ2U=",
                    mediaType = (string?)"image/png",
                    name = (string?)"image.png",
                },
            },
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stream body: {0}", body);
        response.Headers.GetValues("X-Correlation-Id").Should().ContainSingle().Which.Should().Be("corr-static-1");
        body.Should().Contain("runStarted");
        body.Should().Contain("textMessageContent");
        body.Should().Contain("hello from static");
        host.StaticGAgentStreamInvocationPort.Requests.Should().ContainSingle();
        var delegated = host.StaticGAgentStreamInvocationPort.Requests[0];
        delegated.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "default",
        });
        delegated.EndpointId.Should().Be("chat");
        delegated.Input.Prompt.Should().Be("hello static");
        delegated.Input.PreferredActorId.Should().Be(" actor-static-1 ");
        delegated.Input.SessionId.Should().Be("session-1");
        delegated.Input.RevisionId.Should().Be("rev-1");
        delegated.Input.Headers.Should().ContainKey("source").WhoseValue.Should().Be("tests");
        delegated.Input.Caller.Should().NotBeNull();
        delegated.Input.Caller!.ServiceKey.Should().BeEmpty();
        delegated.Input.Timeout.Should().Be(TimeSpan.FromMinutes(2));
        delegated.Input.InputParts.Should().NotBeNull();
        delegated.Input.InputParts!.Should().HaveCount(2);
        delegated.Input.InputParts[0].Kind.Should().Be(GAgentDraftRunInputPartKind.Text);
        delegated.Input.InputParts[0].Text.Should().Be("attachment text");
        delegated.Input.InputParts[1].Kind.Should().Be(GAgentDraftRunInputPartKind.Image);
        delegated.Input.InputParts[1].DataBase64.Should().Be("aW1hZ2U=");
        delegated.Input.InputParts[1].MediaType.Should().Be("image/png");
        delegated.Input.InputParts[1].Name.Should().Be("image.png");
    }

    [Fact]
    public async Task ScopeInvokeStreamEndpoint_ShouldReturnBadRequest_WhenWorkflowEndpointIsNotChat()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var service = BuildService("scope-a", "default", "definition-actor-1");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-1",
                            "rev-1",
                            "definition-actor-1",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "default",
                },
                RevisionId = "rev-1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Command,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "main",
                        WorkflowYaml = "name: main\nsteps:\n  - run: echo hello",
                        DefinitionActorId = "definition-actor-1",
                    },
                },
            },
            CancellationToken.None);

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/invoke/chat:stream", new
        {
            prompt = "hello",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SERVICE_STREAM_REQUEST");
        body["message"].Should().Contain("Only chat endpoints support SSE stream execution.");
    }

    [Fact]
    public async Task ScopeInvokeStreamEndpoint_ShouldReturnBadRequest_WhenWorkflowPayloadTypeDoesNotMatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var service = BuildService("scope-a", "default", "definition-actor-1");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-1",
                            "rev-1",
                            "definition-actor-1",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "default",
                },
                RevisionId = "rev-1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new Google.Protobuf.WellKnownTypes.Empty()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "main",
                        WorkflowYaml = "name: main\nsteps:\n  - run: echo hello",
                        DefinitionActorId = "definition-actor-1",
                    },
                },
            },
            CancellationToken.None);

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/invoke/chat:stream", new
        {
            prompt = "hello",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SERVICE_STREAM_REQUEST");
        body["message"].Should().Contain("expects payload");
    }

    [Fact]
    public void ScopeServiceEndpointHelpers_ShouldRejectWorkflowStream_WhenServiceHasNoActiveDefinitionActor()
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = new ServiceIdentity
            {
                TenantId = "scope-a",
                AppId = "default",
                Namespace = "default",
                ServiceId = "default",
            },
            RevisionId = "rev-1",
            ImplementationKind = ServiceImplementationKind.Workflow,
            Endpoints =
            {
                new ServiceEndpointDescriptor
                {
                    EndpointId = "chat",
                    DisplayName = "chat",
                    Kind = ServiceEndpointKind.Chat,
                    RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                },
            },
        };
        var target = new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                "scope-a:default:default:default",
                "rev-1",
                "dep-1",
                string.Empty,
                "Active",
                []),
            artifact,
            artifact.Endpoints[0]);
        var request = InvokePrivateStatic<ServiceInvocationRequest>(
            "BuildStreamInvocationRequest",
            new ScopeWorkflowCapabilityOptions(),
            "scope-a",
            "default",
            "chat",
            "hello",
            new Dictionary<string, string>(),
            null,
            null,
            null);

        FluentActions.Invoking(() => InvokePrivateStaticVoid("EnsureWorkflowStreamTarget", target, request))
            .Should()
            .Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Workflow service has no active definition actor.*");
    }

    [Fact]
    public async Task ScopeServiceEndpointHelpers_ShouldRejectScriptingStream_WhenRuntimeActorMissing()
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = new ServiceIdentity
            {
                TenantId = "scope-a",
                AppId = "default",
                Namespace = "default",
                ServiceId = "default",
            },
            RevisionId = "rev-1",
            ImplementationKind = ServiceImplementationKind.Scripting,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                ScriptingPlan = new ScriptingServiceDeploymentPlan
                {
                    Revision = "rev-1",
                    DefinitionActorId = "definition-1",
                },
            },
            Endpoints =
            {
                new ServiceEndpointDescriptor
                {
                    EndpointId = "chat",
                    DisplayName = "chat",
                    Kind = ServiceEndpointKind.Chat,
                    RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                },
            },
        };
        var target = new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                "scope-a:default:default:default",
                "rev-1",
                "dep-1",
                string.Empty,
                "Active",
                []),
            artifact,
            artifact.Endpoints[0]);
        var context = new DefaultHttpContext();

        var missingRuntimeAssertion = await FluentActions.Awaiting(() => InvokePrivateStaticTask(
                "HandleScriptingServiceChatStreamAsync",
                context,
                target,
                "hello",
                "session-1",
                "scope-a",
                "default",
                new Dictionary<string, string>(),
                new FakeScriptServiceRunInteractionService
                {
                    StartError = ScriptServiceRunStartError.RuntimeActorUnavailable(
                        "Script runtime actor is not available. The service may not be activated."),
                },
                new ServiceInvocationRequest(),
                CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        missingRuntimeAssertion.Which.Message.Should().Contain("Script runtime actor is not available");
    }

    [Fact]
    public async Task ScopeServiceEndpointHelpers_ShouldRejectScriptingStream_WhenRuntimeActorCannotBeResolved()
    {
        var artifact = new PreparedServiceRevisionArtifact
        {
            Identity = new ServiceIdentity
            {
                TenantId = "scope-a",
                AppId = "default",
                Namespace = "default",
                ServiceId = "default",
            },
            RevisionId = "rev-1",
            ImplementationKind = ServiceImplementationKind.Scripting,
            DeploymentPlan = new ServiceDeploymentPlan
            {
                ScriptingPlan = new ScriptingServiceDeploymentPlan
                {
                    Revision = "rev-1",
                    DefinitionActorId = "definition-1",
                },
            },
            Endpoints =
            {
                new ServiceEndpointDescriptor
                {
                    EndpointId = "chat",
                    DisplayName = "chat",
                    Kind = ServiceEndpointKind.Chat,
                    RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                },
            },
        };
        var target = new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                "scope-a:default:default:default",
                "rev-1",
                "dep-1",
                "script-runtime-1",
                "Active",
                []),
            artifact,
            artifact.Endpoints[0]);
        var context = new DefaultHttpContext();

        var unresolvedRuntimeAssertion = await FluentActions.Awaiting(() => InvokePrivateStaticTask(
                "HandleScriptingServiceChatStreamAsync",
                context,
                target,
                "hello",
                "session-1",
                "scope-a",
                "default",
                new Dictionary<string, string>(),
                new FakeScriptServiceRunInteractionService
                {
                    StartError = ScriptServiceRunStartError.RuntimeActorUnavailable(
                        "Script runtime actor 'script-runtime-1' could not be resolved. The service may not be activated."),
                },
                new ServiceInvocationRequest(),
                CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        unresolvedRuntimeAssertion.Which.Message.Should().Contain("could not be resolved");
    }

    [Fact]
    public async Task InvokeStreamEndpoint_ShouldResolveExplicitServiceAndDelegateToWorkflowPipeline()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var service = BuildService("scope-a", "orders", "definition-actor-orders");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-orders-1",
                            "rev-orders-1",
                            "definition-actor-orders",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-orders-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "orders",
                },
                RevisionId = "rev-orders-1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                        ResponseTypeUrl = Any.Pack(new ChatResponseEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "orders",
                        WorkflowYaml = "name: orders\nsteps:\n  - run: echo orders",
                        DefinitionActorId = "definition-actor-orders",
                    },
                },
            },
            CancellationToken.None);
        host.InteractionService.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-orders", "orders", "cmd-orders", "corr-orders");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);
            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/services/orders/invoke/chat:stream")
        {
            Content = JsonContent.Create(new
            {
                prompt = "hello orders",
                headers = new Dictionary<string, string>
                {
                    ["channel"] = "tests",
                    ["connector.http.authorization"] = "Bearer stale-metadata-token",
                },
            }),
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-orders");
        var response = await host.Client.SendAsync(httpRequest);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stream body: {0}", body);
        body.Should().Contain("aevatar.run.context");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.Source.ActorId.Should().Be("definition-actor-orders");
        host.InteractionService.LastRequest.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.CallerCredential!.BearerToken.Should().Be("token-orders");
        host.InteractionService.LastRequest.Metadata.Should().ContainKey("channel").WhoseValue.Should().Be("tests");
        host.InteractionService.LastRequest.Metadata.Should().NotContainKey("connector.http.authorization");
        host.InteractionService.LastRequest.Headers.Should().ContainKey("channel").WhoseValue.Should().Be("tests");
    }

    [Fact]
    public async Task MemberInvokeStreamEndpoint_ShouldIngestInlineFileIntoTypedFileRef()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.MemberPublishedServiceResolver.Result = new MemberPublishedServiceResolution(
            "scope-a",
            "m-alpha",
            "svc-alpha",
            IsMemberAuthorityBacked: true);
        var service = BuildService("scope-a", "svc-alpha", "wf-alpha");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-alpha-1",
                            "rev-alpha-1",
                            "wf-alpha",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-alpha-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "svc-alpha",
                },
                RevisionId = "rev-alpha-1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                        ResponseTypeUrl = Any.Pack(new ChatResponseEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "file-probe",
                        WorkflowYaml = "name: file_probe\nsteps:\n  - run: echo file",
                        DefinitionActorId = "wf-alpha",
                    },
                },
            },
            CancellationToken.None);
        host.InteractionService.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-alpha", "file-probe", "cmd-alpha", "corr-alpha");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);
            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/scopes/scope-a/members/m-alpha/invoke/chat:stream",
            new
            {
                prompt = "inspect the sanitized attachment",
                inputParts = new[]
                {
                    new
                    {
                        type = "image",
                        inlineFile = new
                        {
                            dataBase64 = "AQID",
                            mediaType = "image/png",
                            name = "probe.png",
                            sizeBytes = 3,
                            ownerScopeId = "scope-a",
                        },
                    },
                },
            },
            "scope-a");
        request.Headers.Add("X-Test-Member-Id", "m-alpha");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stream body: {0}", body);
        host.WorkflowFileIngressPort.Requests.Should().ContainSingle();
        var ingressRequest = host.WorkflowFileIngressPort.Requests[0];
        ingressRequest.Content.ToArray().Should().Equal(new byte[] { 1, 2, 3 });
        ingressRequest.SourceKind.Should().Be(FileArtifactSourceKind.ChatInput);
        ingressRequest.OwnerScopeId.Should().Be("scope-a");
        var part = host.InteractionService.LastRequest!.InputParts.Should().ContainSingle().Which;
        part.DataBase64.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.ArtifactId.Should().Be("workflow-file://file-1");
        part.FileRef.OwnerScopeId.Should().Be("scope-a");
    }

    [Fact]
    public async Task MemberInvokeStreamEndpoint_ShouldAllowEmptyInputForAuthorityBackedWorkflowService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.MemberPublishedServiceResolver.Result = new MemberPublishedServiceResolution(
            "scope-a",
            "m-alpha",
            "svc-alpha",
            IsMemberAuthorityBacked: true);
        var service = BuildService("scope-a", "svc-alpha", "wf-alpha");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-alpha-1",
                            "rev-alpha-1",
                            "wf-alpha",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-alpha-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "svc-alpha",
                },
                RevisionId = "rev-alpha-1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                        ResponseTypeUrl = Any.Pack(new ChatResponseEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "status-report",
                        WorkflowYaml = "name: status_report\nsteps:\n  - run: echo member",
                        DefinitionActorId = "wf-alpha",
                    },
                },
            },
            CancellationToken.None);
        host.InteractionService.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-alpha", "status-report", "cmd-alpha", "corr-alpha");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);
            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/scopes/scope-a/members/m-alpha/invoke/chat:stream",
            new
            {
                prompt = "   ",
                headers = new Dictionary<string, string> { ["channel"] = "member-tests" },
            },
            "scope-a");
        request.Headers.Add("X-Test-Member-Id", "m-alpha");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stream body: {0}", body);
        body.Should().Contain("aevatar.run.context");
        host.MemberPublishedServiceResolver.Calls.Should().ContainSingle()
            .Which.Should().Be(new MemberPublishedServiceResolveRequest("scope-a", "m-alpha"));
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.Prompt.Should().BeEmpty();
        host.InteractionService.LastRequest.Source.ActorId.Should().Be("wf-alpha");
        host.InteractionService.LastRequest.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.Headers.Should().ContainKey("channel").WhoseValue.Should().Be("member-tests");
        host.ServiceRunRegistrationPort.RegisterCalls.Should().ContainSingle()
            .Which.ServiceId.Should().Be("svc-alpha");
    }

    [Fact]
    public async Task MemberInvokeStreamEndpoint_ShouldRejectEmptyInputWithoutAuthorityBackedResolution()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.MemberPublishedServiceResolver.Result = new MemberPublishedServiceResolution(
            "scope-a",
            "m-alpha",
            "svc-alpha");
        var service = BuildService("scope-a", "svc-alpha", "wf-alpha");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-alpha-1",
                            "rev-alpha-1",
                            "wf-alpha",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-alpha-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "svc-alpha",
                },
                RevisionId = "rev-alpha-1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                        ResponseTypeUrl = Any.Pack(new ChatResponseEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "status-report",
                        WorkflowYaml = "name: status_report\nsteps:\n  - run: echo member",
                        DefinitionActorId = "wf-alpha",
                    },
                },
            },
            CancellationToken.None);

        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/scopes/scope-a/members/m-alpha/invoke/chat:stream",
            new
            {
                prompt = "   ",
            },
            "scope-a");
        request.Headers.Add("X-Test-Member-Id", "m-alpha");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "stream body: {0}", body);
        body.Should().Contain("PROMPT_REQUIRED");
        host.MemberPublishedServiceResolver.Calls.Should().ContainSingle()
            .Which.Should().Be(new MemberPublishedServiceResolveRequest("scope-a", "m-alpha"));
        host.InteractionService.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task TeamInvokeStreamEndpoint_ShouldResolveEntryMemberAndDelegateToWorkflowPipeline()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.TeamEntryMemberResolver.Result = new TeamEntryMemberResolution(
            "scope-a",
            "team-a",
            "member-a",
            "member-a");
        var service = BuildService("scope-a", "member-a", "definition-actor-member-a");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-team-member-a-1",
                            "rev-team-member-a-1",
                            "definition-actor-member-a",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-team-member-a-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "member-a",
                },
                RevisionId = "rev-team-member-a-1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                        ResponseTypeUrl = Any.Pack(new ChatResponseEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "member-a",
                        WorkflowYaml = "name: member_a\nsteps:\n  - run: echo member",
                        DefinitionActorId = "definition-actor-member-a",
                    },
                },
            },
            CancellationToken.None);
        host.InteractionService.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-team-a", "member-a", "cmd-team-a", "corr-team-a");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);
            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/teams/team-a/invoke/chat:stream", new
        {
            prompt = "hello team",
            headers = new Dictionary<string, string> { ["channel"] = "team-tests" },
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stream body: {0}", body);
        body.Should().Contain("aevatar.run.context");
        host.TeamEntryMemberResolver.Calls.Should().ContainSingle().Which.Should().Be(("scope-a", "team-a", "chat"));
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.Source.ActorId.Should().Be("definition-actor-member-a");
        host.InteractionService.LastRequest.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.Headers.Should().ContainKey("channel").WhoseValue.Should().Be("team-tests");
    }

    [Fact]
    public async Task TeamInvokeStreamEndpoint_ShouldMapMissingEntryToConflict()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.TeamEntryMemberResolver.Exception = new TeamEntryMemberResolutionException(
            TeamEntryMemberErrorCodes.EntryMemberNotConfigured,
            "scope-a",
            "team-a",
            "team 'team-a' has no entry member configured.");

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/teams/team-a/invoke/chat:stream", new
        {
            prompt = "hello team",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        body.Should().NotBeNull();
        body!["code"].Should().Be(TeamEntryMemberErrorCodes.EntryMemberNotConfigured);
        host.InteractionService.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task TeamInvokeStreamEndpoint_ShouldReturnForbiddenBeforeResolvingEntry_WhenScopeClaimDoesNotMatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/scopes/scope-a/teams/team-a/invoke/chat:stream",
            new
            {
                prompt = "hello team",
            },
            "scope-b");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().NotBeNull();
        body!["code"].Should().Be("SCOPE_ACCESS_DENIED");
        host.TeamEntryMemberResolver.Calls.Should().BeEmpty();
        host.InteractionService.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task InvokeStreamEndpoint_WhenAuthenticationIsDisabled_ShouldExecuteExplicitServiceFlowWithoutClaims()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync(authenticationEnabled: false);
        var service = BuildService("scope-a", "orders", "definition-actor-orders");
        host.ServiceCatalogReader.Service = service;
        host.TrafficViewReader.View = new ServiceTrafficViewSnapshot(
            service.ServiceKey,
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            "dep-orders-1",
                            "rev-orders-1",
                            "definition-actor-orders",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-orders-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "orders",
                },
                RevisionId = "rev-orders-1",
                ImplementationKind = ServiceImplementationKind.Workflow,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "chat",
                        DisplayName = "chat",
                        Kind = ServiceEndpointKind.Chat,
                        RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                        ResponseTypeUrl = Any.Pack(new ChatResponseEvent()).TypeUrl,
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "orders",
                        WorkflowYaml = "name: orders\nsteps:\n  - run: echo orders",
                        DefinitionActorId = "definition-actor-orders",
                    },
                },
            },
            CancellationToken.None);
        host.InteractionService.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-orders", "orders", "cmd-orders", "corr-orders");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);
            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat:stream", new
        {
            prompt = "hello orders",
            headers = new Dictionary<string, string> { ["channel"] = "tests" },
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stream body: {0}", body);
        body.Should().Contain("aevatar.run.context");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.Source.ActorId.Should().Be("definition-actor-orders");
        host.InteractionService.LastRequest.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.Headers.Should().ContainKey("channel").WhoseValue.Should().Be("tests");
    }
}
