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
public sealed class ScopeServiceContractEndpointTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task GetEndpointContractEndpoint_ShouldReturnWorkflowChatStreamContract()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            "scope-a",
            "default",
            "default",
            "default",
            "Orders App",
            "rev-chat",
            "rev-chat",
            "dep-chat",
            "workflow-actor-1",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "Chat",
                    "chat",
                    Any.Pack(new ChatRequestEvent()).TypeUrl,
                    Any.Pack(new ChatResponseEvent()).TypeUrl,
                    "Chat entrypoint"),
            ],
            [],
            DateTimeOffset.UtcNow);
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceRevisionSnapshot(
                    "rev-chat",
                    "workflow",
                    "Published",
                    "hash-chat",
                    string.Empty,
                    [
                        new ServiceEndpointSnapshot(
                            "chat",
                            "Chat",
                            "chat",
                            Any.Pack(new ChatRequestEvent()).TypeUrl,
                            Any.Pack(new ChatResponseEvent()).TypeUrl,
                            "Chat entrypoint"),
                    ],
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    DateTimeOffset.UtcNow.AddMinutes(-9),
                    DateTimeOffset.UtcNow.AddMinutes(-8),
                    null),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceEndpointContractHttpResponse>(
            "/api/scopes/scope-a/services/default/endpoints/chat/contract");

        response.Should().NotBeNull();
        response!.InvokePath.Should().Be("/api/scopes/scope-a/services/default/invoke/chat:stream");
        response.Method.Should().Be("POST");
        response.RequestContentType.Should().Be("application/json");
        response.ResponseContentType.Should().Be("text/event-stream");
        response.SupportsSse.Should().BeTrue();
        response.SupportsAguiFrames.Should().BeFalse();
        response.StreamFrameFormat.Should().Be("workflow-run-event");
        response.DefaultSmokeInputMode.Should().Be("prompt");
        response.DefaultSmokePrompt.Should().Be("Hello from Studio Bind.");
        response.SampleRequestJson.Should().BeNull();
        response.RevisionId.Should().Be("rev-chat");
        response.CurlExample.Should().Contain("Accept: text/event-stream");
        response.FetchExample.Should().Contain("prompt");
    }

    [Fact]
    public async Task GetEndpointContractEndpoint_ShouldPreferServingRevisionThatContainsRequestedEndpoint()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            "scope-a",
            "default",
            "default",
            "default",
            "Orders App",
            "rev-default",
            "rev-active",
            "dep-chat",
            "static-actor-1",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "Chat",
                    "chat",
                    Any.Pack(new ChatRequestEvent()).TypeUrl,
                    Any.Pack(new ChatResponseEvent()).TypeUrl,
                    "Chat entrypoint"),
            ],
            [],
            DateTimeOffset.UtcNow);
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceRevisionSnapshot(
                    "rev-default",
                    ServiceImplementationKind.Workflow.ToString(),
                    "Published",
                    "hash-default",
                    string.Empty,
                    [
                        new ServiceEndpointSnapshot(
                            "legacy",
                            "Legacy",
                            "chat",
                            Any.Pack(new ChatRequestEvent()).TypeUrl,
                            Any.Pack(new ChatResponseEvent()).TypeUrl,
                            "Legacy endpoint"),
                    ],
                    DateTimeOffset.UtcNow.AddMinutes(-12),
                    DateTimeOffset.UtcNow.AddMinutes(-11),
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    null),
                new ServiceRevisionSnapshot(
                    "rev-active",
                    ServiceImplementationKind.Static.ToString(),
                    "Published",
                    "hash-active",
                    string.Empty,
                    [
                        new ServiceEndpointSnapshot(
                            "chat",
                            "Chat",
                            "chat",
                            Any.Pack(new ChatRequestEvent()).TypeUrl,
                            Any.Pack(new ChatResponseEvent()).TypeUrl,
                            "Chat entrypoint"),
                    ],
                    DateTimeOffset.UtcNow.AddMinutes(-9),
                    DateTimeOffset.UtcNow.AddMinutes(-8),
                    DateTimeOffset.UtcNow.AddMinutes(-7),
                    null),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceEndpointContractHttpResponse>(
            "/api/scopes/scope-a/services/default/endpoints/chat/contract");

        response.Should().NotBeNull();
        response!.RevisionId.Should().Be("rev-active");
        response.SupportsAguiFrames.Should().BeTrue();
        response.StreamFrameFormat.Should().Be("agui");
    }

    [Fact]
    public async Task GetEndpointContractEndpoint_ShouldReturnAguiStreamContractForStaticChatEndpoint()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            "scope-a",
            "default",
            "default",
            "default",
            "Orders App",
            "rev-chat",
            "rev-chat",
            "dep-chat",
            "static-actor-1",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "Chat",
                    "chat",
                    Any.Pack(new ChatRequestEvent()).TypeUrl,
                    Any.Pack(new ChatResponseEvent()).TypeUrl,
                    "Chat entrypoint"),
            ],
            [],
            DateTimeOffset.UtcNow);
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceRevisionSnapshot(
                    "rev-chat",
                    ServiceImplementationKind.Static.ToString(),
                    "Published",
                    "hash-chat",
                    string.Empty,
                    [
                        new ServiceEndpointSnapshot(
                            "chat",
                            "Chat",
                            "chat",
                            Any.Pack(new ChatRequestEvent()).TypeUrl,
                            Any.Pack(new ChatResponseEvent()).TypeUrl,
                            "Chat entrypoint"),
                    ],
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    DateTimeOffset.UtcNow.AddMinutes(-9),
                    DateTimeOffset.UtcNow.AddMinutes(-8),
                    null),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceEndpointContractHttpResponse>(
            "/api/scopes/scope-a/services/default/endpoints/chat/contract");

        response.Should().NotBeNull();
        response!.SupportsSse.Should().BeTrue();
        response.SupportsAguiFrames.Should().BeTrue();
        response.StreamFrameFormat.Should().Be("agui");
    }

    [Fact]
    public async Task GetEndpointContractEndpoint_ShouldReturnTypedInvokeContractForCommandEndpoint()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            "scope-a",
            "default",
            "default",
            "default",
            "Orders App",
            "rev-cmd",
            "rev-cmd",
            "dep-cmd",
            "gagent-actor-1",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "run",
                    "Run",
                    "command",
                    Any.Pack(new StringValue()).TypeUrl,
                    string.Empty,
                    "Run command"),
            ],
            [],
            DateTimeOffset.UtcNow);
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceRevisionSnapshot(
                    "rev-cmd",
                    ServiceImplementationKind.Static.ToString(),
                    "Published",
                    "hash-cmd",
                    string.Empty,
                    [
                        new ServiceEndpointSnapshot(
                            "run",
                            "Run",
                            "command",
                            Any.Pack(new StringValue()).TypeUrl,
                            string.Empty,
                            "Run command"),
                    ],
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    DateTimeOffset.UtcNow.AddMinutes(-9),
                    DateTimeOffset.UtcNow.AddMinutes(-8),
                    null),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceEndpointContractHttpResponse>(
            "/api/scopes/scope-a/services/default/endpoints/run/contract");

        response.Should().NotBeNull();
        response!.InvokePath.Should().Be("/api/scopes/scope-a/services/default/invoke/run");
        response.ResponseContentType.Should().Be("application/json");
        response.SupportsSse.Should().BeFalse();
        response.SupportsAguiFrames.Should().BeFalse();
        response.StreamFrameFormat.Should().BeNull();
        response.DefaultSmokeInputMode.Should().Be("typed-payload");
        response.DefaultSmokePrompt.Should().BeNull();
        response.SampleRequestJson.Should().Contain("payloadTypeUrl");
        response.SampleRequestJson.Should().Contain("StringValue");
        response.CurlExample.Should().Contain("payloadBase64");
        response.FetchExample.Should().Contain("payloadTypeUrl");
        response.RevisionId.Should().Be("rev-cmd");
    }

    [Fact]
    public async Task GetEndpointContractEndpoint_ShouldReturnBadRequest_WhenEndpointIdIsBlank()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.GetAsync("/api/scopes/scope-a/services/default/endpoints/%20/contract");
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_ENDPOINT_ID");
        body["message"].Should().Be("endpointId is required.");
    }

    [Fact]
    public async Task GetEndpointContractEndpoint_ShouldForwardAppIdToLifecycleQueries()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = new ServiceCatalogSnapshot(
            "scope-a:custom-app:default:orders",
            "scope-a",
            "custom-app",
            "default",
            "orders",
            "Orders App",
            "rev-run",
            "rev-run",
            "dep-run",
            "actor-1",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "run",
                    "Run",
                    "command",
                    Any.Pack(new StringValue()).TypeUrl,
                    string.Empty,
                    "Run command"),
            ],
            [],
            DateTimeOffset.UtcNow);
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:custom-app:default:orders",
            [
                new ServiceRevisionSnapshot(
                    "rev-run",
                    ServiceImplementationKind.Static.ToString(),
                    "Published",
                    "hash-run",
                    string.Empty,
                    [
                        new ServiceEndpointSnapshot(
                            "run",
                            "Run",
                            "command",
                            Any.Pack(new StringValue()).TypeUrl,
                            string.Empty,
                            "Run command"),
                    ],
                    DateTimeOffset.UtcNow.AddMinutes(-10),
                    DateTimeOffset.UtcNow.AddMinutes(-9),
                    DateTimeOffset.UtcNow.AddMinutes(-8),
                    null),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceEndpointContractHttpResponse>(
            "/api/scopes/scope-a/services/orders/endpoints/run/contract?appId=custom-app");

        response.Should().NotBeNull();
        host.LifecycleQueryPort.LastServiceIdentity.Should().NotBeNull();
        host.LifecycleQueryPort.LastServiceIdentity!.AppId.Should().Be("custom-app");
        host.LifecycleQueryPort.LastRevisionsIdentity.Should().NotBeNull();
        host.LifecycleQueryPort.LastRevisionsIdentity!.AppId.Should().Be("custom-app");
    }

    [Fact]
    public async Task GetEndpointContractEndpoint_ShouldReturnNotFound_WhenEndpointDoesNotExist()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            "scope-a",
            "default",
            "default",
            "default",
            "Orders App",
            "rev-chat",
            "rev-chat",
            "dep-chat",
            "workflow-actor-1",
            "Active",
            [
                new ServiceEndpointSnapshot(
                    "chat",
                    "Chat",
                    "chat",
                    Any.Pack(new ChatRequestEvent()).TypeUrl,
                    Any.Pack(new ChatResponseEvent()).TypeUrl,
                    "Chat entrypoint"),
            ],
            [],
            DateTimeOffset.UtcNow);
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetAsync(
            "/api/scopes/scope-a/services/default/endpoints/nonexistent/contract");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body.Should().NotBeNull();
        body!["code"].Should().Be("SCOPE_SERVICE_ENDPOINT_CONTRACT_NOT_FOUND");
        body["message"].Should().Contain("nonexistent");
    }
}
