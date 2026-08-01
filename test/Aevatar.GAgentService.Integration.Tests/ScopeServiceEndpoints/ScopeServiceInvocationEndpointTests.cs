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
public sealed class ScopeServiceInvocationEndpointTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task BindingEndpoints_ShouldMapScopeToInternalServiceIdentity()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.QueryPort.BindingsResult = new ServiceBindingCatalogSnapshot(
            "scope-a:default:default:orders",
            [],
            DateTimeOffset.UtcNow);

        var createResponse = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/bindings", new
        {
            bindingId = "binding-a",
            displayName = "Dependency",
            bindingKind = "service",
            service = new
            {
                serviceId = "dependency",
                endpointId = "run",
            },
        });
        var getResponse = await host.Client.GetFromJsonAsync<ServiceBindingCatalogSnapshot>("/api/scopes/scope-a/services/orders/bindings");

        createResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        getResponse.Should().NotBeNull();
        host.CommandPort.CreateBindingCommand.Should().NotBeNull();
        host.CommandPort.CreateBindingCommand!.Spec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "orders",
        });
        host.CommandPort.CreateBindingCommand.Spec.ServiceRef!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "dependency",
        });
        host.QueryPort.LastBindingsIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "orders",
        });
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldMapScopeToInternalServiceIdentity()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Be("/api/scopes/scope-a/services/orders/runs/run-1");
        var receipt = await response.Content.ReadFromJsonAsync<ServiceInvocationAcceptedReceipt>();
        receipt.Should().NotBeNull();
        receipt!.StatusUrl.Should().Be("/api/scopes/scope-a/services/orders/runs/run-1");
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "orders",
        });
        host.InvocationPort.LastRequest.EndpointId.Should().Be("chat");
        host.InvocationPort.LastRequest.Payload.TypeUrl.Should().Be("type.googleapis.com/google.protobuf.Empty");
    }

    [Fact]
    public async Task DefaultInvokeEndpoint_ShouldMapScopeToDefaultServiceIdentity()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/invoke/run", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Be("/api/scopes/scope-a/services/default/runs/run-1");
        var receipt = await response.Content.ReadFromJsonAsync<ServiceInvocationAcceptedReceipt>();
        receipt.Should().NotBeNull();
        receipt!.StatusUrl.Should().Be("/api/scopes/scope-a/services/default/runs/run-1");
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "default",
        });
        host.InvocationPort.LastRequest.EndpointId.Should().Be("run");
    }

    [Fact]
    public async Task MemberInvokeEndpoint_ShouldMapMemberToPublishedServiceIdentity()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/scopes/scope-a/members/member-a/invoke/chat",
            new
            {
                payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
                payloadBase64 = "",
            },
            "scope-a");
        request.Headers.Add("X-Test-Member-Id", "member-a");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Be("/api/scopes/scope-a/services/member-a/runs/run-1");
        var receipt = await response.Content.ReadFromJsonAsync<ServiceInvocationAcceptedReceipt>();
        receipt.Should().NotBeNull();
        receipt!.StatusUrl.Should().Be("/api/scopes/scope-a/services/member-a/runs/run-1");
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "member-a",
        });
        host.InvocationPort.LastRequest.EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task TeamInvokeEndpoint_ShouldMapTeamEntryToPublishedServiceIdentity()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.TeamEntryMemberResolver.Result = new TeamEntryMemberResolution(
            "scope-a",
            "team-a",
            "member-a",
            "member-a");

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/teams/team-a/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.OriginalString.Should().Be("/api/scopes/scope-a/members/member-a/runs/run-1");
        var receipt = await response.Content.ReadFromJsonAsync<ServiceInvocationAcceptedReceipt>();
        receipt.Should().NotBeNull();
        receipt!.StatusUrl.Should().Be("/api/scopes/scope-a/members/member-a/runs/run-1");
        host.TeamEntryMemberResolver.Calls.Should().ContainSingle().Which.Should().Be(("scope-a", "team-a", "chat"));
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "member-a",
        });
        host.InvocationPort.LastRequest.EndpointId.Should().Be("chat");
    }

    [Fact]
    public async Task TeamInvokeEndpoint_ShouldMapMissingTeamToNotFound()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.TeamEntryMemberResolver.Exception = new TeamEntryMemberResolutionException(
            TeamEntryMemberErrorCodes.TeamNotFound,
            "scope-a",
            "team-missing",
            "team 'team-missing' not found.");

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/teams/team-missing/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotBeNull();
        body!["code"].Should().Be(TeamEntryMemberErrorCodes.TeamNotFound);
        host.InvocationPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task TeamInvokeEndpoint_ShouldMapEntryMemberFailureToConflict()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.TeamEntryMemberResolver.Exception = new TeamEntryMemberResolutionException(
            TeamEntryMemberErrorCodes.EntryMemberNotReady,
            "scope-a",
            "team-a",
            "entry member 'member-a' is not bind-ready.");

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/teams/team-a/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        body.Should().NotBeNull();
        body!["code"].Should().Be(TeamEntryMemberErrorCodes.EntryMemberNotReady);
        host.InvocationPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldForwardExplicitRevisionId()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            revisionId = "rev-2",
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.RevisionId.Should().Be("rev-2");
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldReturnBadRequest_WhenPayloadBase64IsInvalid()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "not-base64",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SCOPE_SERVICE_INVOKE_REQUEST");
        body["message"].Should().Contain("valid base64");
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldReturnNotFound_WhenInvocationTargetIsMissing()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.InvocationPort.ExceptionFactory = _ => new InvalidOperationException("Service 'scope-a:default:default:orders' was not found.");

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotBeNull();
        body!["code"].Should().Be("SCOPE_SERVICE_INVOKE_TARGET_NOT_FOUND");
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldReturnConflict_WhenInvocationTargetIsUnavailable()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.InvocationPort.ExceptionFactory = _ => new InvalidOperationException("No active serving targets are available.");

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        body.Should().NotBeNull();
        body!["code"].Should().Be("SCOPE_SERVICE_INVOKE_TARGET_UNAVAILABLE");
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldReturnInvokeUnavailableBody_WhenReadinessRejectsInvocation()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.InvocationPort.ExceptionFactory = _ => new ServiceInvokeReadinessException(
            "Endpoint 'chat' is not invoke-ready.",
            new ServiceInvokeReadinessSnapshot(
                "scope-a:default:default:orders",
                "chat",
                ServiceInvokeReadinessStatus.Unavailable,
                ServiceInvokeUnavailableReason.RevisionNotPrepared,
                "rev-2",
                "dep-2",
                "actor-2",
                DateTimeOffset.Parse("2026-06-05T03:00:00+00:00"),
                13,
                "evt-13",
                11,
                12,
                13));

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].GetString().Should().Be("SERVICE_INVOKE_UNAVAILABLE");
        body["message"].GetString().Should().Be("Endpoint 'chat' is not invoke-ready.");
        body["readinessStatus"].GetString().Should().Be(ServiceInvokeReadinessStatus.Unavailable.ToString());
        body["reason"].GetString().Should().Be(ServiceInvokeUnavailableReason.RevisionNotPrepared.ToString());
        body["serviceKey"].GetString().Should().Be("scope-a:default:default:orders");
        body["revisionId"].GetString().Should().Be("rev-2");
        body["endpointId"].GetString().Should().Be("chat");
        body["deploymentId"].GetString().Should().Be("dep-2");
        body["observedAt"].GetDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-06-05T03:00:00+00:00"));
        body["aggregateStateVersion"].GetInt64().Should().Be(13);
        body["sourceCatalogVersion"].GetInt64().Should().Be(11);
        body["sourceServingVersion"].GetInt64().Should().Be(12);
        body["sourceRevisionVersion"].GetInt64().Should().Be(13);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Identity.ServiceId.Should().Be("orders");
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldPackPayloadJson_AsTypedAny_UsingExplicitRevision()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        await host.RevisionCatalog.UpsertRevisionAsync(
            "scope-a:default:default:orders",
            "rev-1",
            new PreparedServiceRevisionArtifact
            {
                ProtocolDescriptorSet = ScopeBuildProtocolDescriptorSetFor(ServiceIdentity.Descriptor),
            });

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            revisionId = "rev-1",
            payloadTypeUrl = "type.googleapis.com/aevatar.gagentservice.ServiceIdentity",
            payloadJson = """{"tenantId":"hello-tenant","serviceId":"orders"}""",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.RevisionId.Should().Be("rev-1");
        host.InvocationPort.LastRequest.Payload.TypeUrl.Should().Be("type.googleapis.com/aevatar.gagentservice.ServiceIdentity");
        var decoded = ServiceIdentity.Parser.ParseFrom(host.InvocationPort.LastRequest.Payload.Value);
        decoded.TenantId.Should().Be("hello-tenant");
        decoded.ServiceId.Should().Be("orders");
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldReturnBadRequest_WhenPayloadJsonAndPayloadBase64BothSet()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/aevatar.gagentservice.ServiceIdentity",
            payloadBase64 = "AAAA",
            payloadJson = """{"tenantId":"x"}""",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["code"].Should().Be("INVALID_SCOPE_SERVICE_INVOKE_REQUEST");
        body["message"].Should().Contain("mutually exclusive");
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldReturnBadRequest_WhenPayloadJsonTypeUrlMissingFromRevision()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        await host.RevisionCatalog.UpsertRevisionAsync(
            "scope-a:default:default:orders",
            "rev-1",
            new PreparedServiceRevisionArtifact
            {
                ProtocolDescriptorSet = ScopeBuildProtocolDescriptorSetFor(ServiceIdentity.Descriptor),
            });

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            revisionId = "rev-1",
            payloadTypeUrl = "type.googleapis.com/demo.Unknown",
            payloadJson = """{"foo":"bar"}""",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["code"].Should().Be("INVALID_SCOPE_SERVICE_INVOKE_REQUEST");
        body["message"].Should().Contain("not found in revision");
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldReturnBadRequest_WhenPayloadJsonIsMalformed()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        await host.RevisionCatalog.UpsertRevisionAsync(
            "scope-a:default:default:orders",
            "rev-1",
            new PreparedServiceRevisionArtifact
            {
                ProtocolDescriptorSet = ScopeBuildProtocolDescriptorSetFor(ServiceIdentity.Descriptor),
            });

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            revisionId = "rev-1",
            payloadTypeUrl = "type.googleapis.com/aevatar.gagentservice.ServiceIdentity",
            payloadJson = "{this is not json",
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["code"].Should().Be("INVALID_SCOPE_SERVICE_INVOKE_REQUEST");
        body["message"].Should().Contain("payloadJson");
    }
}
