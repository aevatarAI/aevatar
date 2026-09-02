using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Application.Bindings;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.AGUI.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

[Collection(ScopeServiceEndpointCollection.Name)]
public sealed class ScopeServiceDraftRunEndpointTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldDelegateInlineWorkflowBundleToWorkflowPipeline()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.InteractionService.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "main", "cmd-1", "corr-1");
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
        };

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/workflow/draft-run", new
        {
            prompt = "run the draft",
            workflowYamls = new[]
            {
                "name: main\nsteps:\n  - run: echo hello",
                "name: child\nsteps:\n  - run: echo child",
            },
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("aevatar.run.context");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.Source.WorkflowYamls.Should().NotBeNull();
        host.InteractionService.LastRequest.Source.WorkflowYamls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldEmitAguiEvents_WhenRequested()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.InteractionService.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "main", "cmd-1", "corr-1");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            await emitAsync(new WorkflowRunEventEnvelope
            {
                Custom = new WorkflowCustomEventPayload
                {
                    Name = "aevatar.human_input.request",
                    Payload = Any.Pack(new WorkflowHumanInputRequestCustomPayload
                    {
                        StepId = "approve",
                        RunId = "run-1",
                        SuspensionType = "human_input",
                        Prompt = "Need approval",
                        TimeoutSeconds = 30,
                        VariableName = "decision",
                    }),
                },
            }, ct);

            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/workflow/draft-run")
        {
            Content = JsonContent.Create(new
            {
                prompt = "run the draft",
                workflowYamls = new[]
                {
                    "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant",
                },
                eventFormat = "agui",
                headers = new Dictionary<string, string>
                {
                    ["connector.http.authorization"] = "Bearer stale-metadata-token",
                },
            }),
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token-123");
        var response = await host.Client.SendAsync(httpRequest);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"humanInputRequest\"");
        body.Should().Contain("aevatar.run.context");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.Source.WorkflowYamls.Should().HaveCount(1);
        host.InteractionService.LastRequest.CallerCredential!.BearerToken.Should().Be("token-123");
        host.InteractionService.LastRequest.Metadata.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldIngestAguiJsonInlineFileIntoTypedFileRef()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.InteractionService.ResultFactory = async (_, _, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "main", "cmd-1", "corr-1");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Completed,
                    true));
        };

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/workflow/draft-run", new
        {
            prompt = "inspect the sanitized attachment",
            workflowYamls = new[]
            {
                "name: main\nsteps:\n  - run: echo file",
            },
            eventFormat = "agui",
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
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", body);
        host.WorkflowFileIngressPort.Requests.Should().ContainSingle();
        host.WorkflowFileIngressPort.Requests[0].Content.ToArray().Should().Equal(new byte[] { 1, 2, 3 });
        var part = host.InteractionService.LastRequest!.InputParts.Should().ContainSingle().Which;
        part.DataBase64.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.ArtifactId.Should().Be("workflow-file://file-1");
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldPropagateScopedPreferredLlmRouteToAguiRequest()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync(
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
        host.InteractionService.ResultFactory = async (_, _, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "main", "cmd-1", "corr-1");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/workflow/draft-run", new
        {
            prompt = "run the draft",
            workflowYamls = new[]
            {
                "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant",
            },
            eventFormat = "agui",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.LlmControl.Should().NotBeNull();
        host.InteractionService.LastRequest.LlmControl!.RoutePreference.Should().Be("/preferred-route");
        host.InteractionService.LastRequest.LlmControl.ModelOverride.Should().BeNull();
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldReturnBadRequest_WhenEventFormatIsInvalid()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/workflow/draft-run", new
        {
            prompt = "run the draft",
            workflowYamls = new[]
            {
                "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant",
            },
            eventFormat = "invalid",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SCOPE_DRAFT_RUN_REQUEST");
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldReturnBadRequest_WhenWorkflowYamlsAreMissing()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/workflow/draft-run", new
        {
            prompt = "run the draft",
            workflowYamls = Array.Empty<string>(),
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SCOPE_DRAFT_RUN_REQUEST");
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldReturnWorkflowYamlValidationMessage_WhenInlineYamlIsInvalid()
    {
        const string invalidWorkflowYaml = "name: main\nsteps:\n- id: call\n  type: tool_call";
        const string validationMessage = "Workflow step 'call' external capability is invalid: nyxid_proxy derived field 'service_id' cannot be authored; select a connected-service operation and rebind.";
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.WorkflowDefinitionParser.ParseResults[invalidWorkflowYaml] = WorkflowYamlParseResult.Invalid(validationMessage);

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/workflow/draft-run", new
        {
            prompt = "run the draft",
            workflowYamls = new[]
            {
                invalidWorkflowYaml,
            },
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_WORKFLOW_YAML");
        body["message"].Should().Be(validationMessage);
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.Source.WorkflowYamls.Should().ContainSingle();
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldDelegateInlineWorkflowBundleValidationToWorkflowPipeline()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/workflow/draft-run", new
        {
            prompt = "run the draft",
            workflowYamls = new[]
            {
                "name: main\nsteps:\n  - run: echo hello",
                "name: main\nsteps:\n  - run: echo child",
            },
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_WORKFLOW_YAML");
        body["message"].Should().Be("Duplicate workflow name 'main' in workflowYamls.");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.Source.WorkflowYamls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldReturnExternalCapabilityReadiness_WhenInlineYamlCapabilityIsInvalid()
    {
        const string invalidWorkflowYaml = "name: main\nsteps:\n- id: call\n  type: tool_call";
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.WorkflowDefinitionParser.ParseResults[invalidWorkflowYaml] = WorkflowYamlParseResult.Invalid(
            "Workflow step 'call' external capability is invalid: select a connected-service operation and rebind.",
            new ExternalCapabilityReadiness
            {
                Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
                Blockers =
                {
                    new ExternalCapabilityBlocker
                    {
                        Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
                        Code = "admission_rebind_required",
                        SafeMessage = "Select a connected-service operation and rebind.",
                    },
                },
                Remediations =
                {
                    new ExternalCapabilityRemediation
                    {
                        ActionKind = ExternalCapabilityRemediationActionKind.RebindWorkflow,
                        Label = "Rebind workflow",
                        TrustedLocator = "nyxid:services",
                    },
                },
                Sources =
                {
                    new ExternalCapabilitySourceStamp
                    {
                        SourceKind = ExternalCapabilitySourceKind.NyxIdMcpConfig,
                        SourceId = "nyxid-mcp-config:caller:nyx-user-alpha",
                        SourceVersion = 0,
                    },
                },
                SelectedSelector = new ExternalWorkflowCapabilitySelector
                {
                    NyxIdOperation = new NyxIdOperationSelector
                    {
                        UserServiceId = "user-service-1",
                        EndpointId = "endpoint-1",
                    },
                },
            });

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/workflow/draft-run", new
        {
            prompt = "run the draft",
            workflowYamls = new[]
            {
                invalidWorkflowYaml,
            },
        });
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("code").GetString().Should().Be("INVALID_WORKFLOW_YAML");
        var readiness = body.RootElement.GetProperty("externalCapabilityReadiness");
        readiness.GetProperty("status").GetString().Should().Be("admission_rebind_required");
        readiness.GetProperty("blockers")[0].GetProperty("code").GetString().Should().Be("admission_rebind_required");
        readiness.GetProperty("selectedCapability").GetProperty("userServiceId").GetString().Should().Be("user-service-1");
        readiness.GetProperty("selectedCapability").GetProperty("endpointId").GetString().Should().Be("endpoint-1");
        readiness.GetProperty("selectedCapability").GetProperty("operationId").ValueKind.Should().Be(JsonValueKind.Null);
        readiness.GetProperty("remediations")[0].GetProperty("actionKind").GetString().Should().Be("rebind_workflow");
        readiness.GetProperty("remediations")[0].GetProperty("trustedLocator").GetString().Should().Be("nyxid:services");
        readiness.GetProperty("sources")[0].GetProperty("sourceKind").GetString().Should().Be("nyx_id_mcp_config");
        readiness.GetProperty("sources")[0].GetProperty("sourceVersion").GetInt64().Should().Be(0);
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.Source.WorkflowYamls.Should().ContainSingle();
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldReturnInvalidCallerCredential_WhenBearerIsMalformed()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/workflow/draft-run")
        {
            Content = JsonContent.Create(new
            {
                prompt = "run the draft",
                workflowYamls = new[]
                {
                    "name: main\nroles:\n  - id: assistant\n    name: Assistant\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: assistant",
                },
            }),
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token 123");

        var response = await host.Client.SendAsync(httpRequest);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_CALLER_CREDENTIAL");
        body["message"].Should().Be("Caller credential is invalid.");
        host.InteractionService.LastRequest.Should().BeNull();
    }
}
