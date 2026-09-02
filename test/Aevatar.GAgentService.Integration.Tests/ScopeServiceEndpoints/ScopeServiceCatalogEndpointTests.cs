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
public sealed class ScopeServiceCatalogEndpointTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task ListScopeServicesEndpoint_ShouldReturnScopeServiceCatalog()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Services =
        [
            new ServiceCatalogSnapshot(
                "scope-a:default:default:orders",
                "scope-a",
                "default",
                "default",
                "orders",
                "Orders",
                "rev-1",
                "rev-1",
                "dep-1",
                "orders-actor",
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
                DateTimeOffset.UtcNow,
                new ServiceExternalExposureSnapshot(
                    "aevatar-orders",
                    DateTimeOffset.Parse("2026-06-11T01:02:03+00:00"),
                    SourceStateVersion: 44)),
            new ServiceCatalogSnapshot(
                "scope-a:default:default:billing",
                "scope-a",
                "default",
                "default",
                "billing",
                "Billing",
                "rev-2",
                "rev-2",
                "dep-2",
                "billing-actor",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "charge",
                        "Charge",
                        "command",
                        Any.Pack(new StringValue()).TypeUrl,
                        string.Empty,
                        "Charge command"),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceCatalogSnapshot(
                "scope-a:default:default:search",
                "scope-a",
                "default",
                "default",
                "search",
                "Search",
                "rev-3",
                "rev-3",
                "dep-3",
                "search-actor",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "query",
                        "Query",
                        "command",
                        Any.Pack(new StringValue()).TypeUrl,
                        string.Empty,
                        "Query command"),
                ],
                [],
                DateTimeOffset.UtcNow),
            new ServiceCatalogSnapshot(
                "scope-a:default:default:audit",
                "scope-a",
                "default",
                "default",
                "audit",
                "Audit",
                "rev-4",
                "rev-4",
                "dep-4",
                "audit-actor",
                "Active",
                [
                    new ServiceEndpointSnapshot(
                        "archive",
                        "Archive",
                        "command",
                        Any.Pack(new StringValue()).TypeUrl,
                        string.Empty,
                        "Archive command"),
                ],
                [],
                DateTimeOffset.UtcNow),
        ];
        host.InvocationCatalogReader.CatalogsByServiceKey["scope-a:default:default:orders"] = BuildScopeInvocationCatalog(
            "scope-a:default:default:orders",
            "run",
            ServiceInvokeReadinessStatus.Ready,
            ServiceInvokeUnavailableReason.Unspecified,
            "rev-1",
            "dep-1",
            "orders-actor",
            31);
        host.InvocationCatalogReader.CatalogsByServiceKey["scope-a:default:default:billing"] = BuildScopeInvocationCatalog(
            "scope-a:default:default:billing",
            "charge",
            ServiceInvokeReadinessStatus.Unavailable,
            ServiceInvokeUnavailableReason.RevisionNotPrepared,
            "rev-2",
            "dep-2",
            "billing-actor",
            32);
        host.InvocationCatalogReader.CatalogsByServiceKey["scope-a:default:default:audit"] = new ServiceInvocationCatalogSnapshot(
            "scope-a:default:default:audit",
            [],
            DateTimeOffset.Parse("2026-03-14T00:06:00+00:00"),
            33,
            "evt-33",
            30,
            31,
            32);

        var response = await host.Client.GetAsync("/api/scopes/scope-a/services?take=25");
        var body = await response.Content.ReadFromJsonAsync<IReadOnlyList<ScopeServiceEndpoints.ScopeServiceHttpResponse>>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.Should().HaveCount(4);
        body.Single(x => x.ServiceId == "orders").Endpoints.Should().ContainSingle(x => x.EndpointId == "run");
        body.Single(x => x.ServiceId == "orders").ExternalExposure.Should().NotBeNull();
        body.Single(x => x.ServiceId == "orders").ExternalExposure!.NyxidSlug.Should().Be("aevatar-orders");
        body.Single(x => x.ServiceId == "orders").ExternalExposure!.RegisteredAt.Should()
            .Be(DateTimeOffset.Parse("2026-06-11T01:02:03+00:00"));
        body.Single(x => x.ServiceId == "orders").ExternalExposure!.SourceStateVersion.Should().Be(44);
        body.Single(x => x.ServiceId == "orders").InvokeReady.Should().BeTrue();
        body.Single(x => x.ServiceId == "orders").InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Ready.ToString());
        body.Single(x => x.ServiceId == "orders").InvokeUnavailableReason.Should().BeNull();
        body.Single(x => x.ServiceId == "billing").InvokeReady.Should().BeFalse();
        body.Single(x => x.ServiceId == "billing").InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable.ToString());
        body.Single(x => x.ServiceId == "billing").InvokeUnavailableReason.Should().Be(ServiceInvokeUnavailableReason.RevisionNotPrepared.ToString());
        body.Single(x => x.ServiceId == "search").InvokeReady.Should().BeFalse();
        body.Single(x => x.ServiceId == "search").InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unspecified.ToString());
        body.Single(x => x.ServiceId == "search").InvokeUnavailableReason.Should().BeNull();
        body.Single(x => x.ServiceId == "audit").InvokeReady.Should().BeFalse();
        body.Single(x => x.ServiceId == "audit").InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unspecified.ToString());
        body.Single(x => x.ServiceId == "audit").InvokeUnavailableReason.Should().BeNull();
        host.LifecycleQueryPort.LastListTenantId.Should().Be("scope-a");
        host.LifecycleQueryPort.LastListAppId.Should().Be("default");
        host.LifecycleQueryPort.LastListNamespace.Should().Be("default");
        host.LifecycleQueryPort.LastListTake.Should().Be(25);
    }

    [Fact]
    public async Task ListScopeServicesEndpoint_ShouldUseExplicitAppId()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.GetAsync("/api/scopes/scope-a/services?appId=%20customApp%20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.LifecycleQueryPort.LastListTenantId.Should().Be("scope-a");
        host.LifecycleQueryPort.LastListAppId.Should().Be("customApp");
        host.LifecycleQueryPort.LastListNamespace.Should().Be("default");
        host.LifecycleQueryPort.LastListTake.Should().Be(200);
    }

    [Fact]
    public async Task ListScopeServicesEndpoint_ShouldRejectMismatchedAuthenticatedScope()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/scopes/scope-a/services");
        request.Headers.Add("X-Test-Scope-Id", "scope-b");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.LifecycleQueryPort.LastListTenantId.Should().BeNull();
    }

    [Fact]
    public async Task GetBindingEndpoint_ShouldReturnDefaultScopeBindingSummary()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            "scope-a",
            "default",
            "default",
            "default",
            "Orders App",
            "rev-2",
            "rev-2",
            "dep-2",
            "def-actor-2",
            "Active",
            [],
            [],
            DateTimeOffset.UtcNow);
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceRevisionSnapshot(
                    "rev-1",
                    "workflow",
                    "Published",
                    "hash-1",
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    null),
                new ServiceRevisionSnapshot(
                    "rev-2",
                    "workflow",
                    "Published",
                    "hash-2",
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    null),
            ],
            DateTimeOffset.UtcNow);
        host.ServingQueryPort.ServingSet = new ServiceServingSetSnapshot(
            "scope-a:default:default:default",
            2,
            string.Empty,
            [
                new ServiceServingTargetSnapshot(
                    "dep-2",
                    "rev-2",
                    "def-actor-2",
                    100,
                    "Active",
                    []),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeBindingStatusHttpResponse>("/api/scopes/scope-a/binding");

        response.Should().NotBeNull();
        response!.Available.Should().BeTrue();
        response.ScopeId.Should().Be("scope-a");
        response.ServiceId.Should().Be("default");
        response.DisplayName.Should().Be("Orders App");
        response.Revisions.Should().HaveCount(2);
        response.Revisions[0].RevisionId.Should().Be("rev-2");
        response.Revisions[0].IsDefaultServing.Should().BeTrue();
        response.Revisions[0].IsActiveServing.Should().BeTrue();
        response.Revisions[0].IsServingTarget.Should().BeTrue();
        response.Revisions[0].DeploymentId.Should().Be("dep-2");
    }

    [Fact]
    public async Task GetBindingEndpoint_ShouldPreferActiveServingTarget_WhenRevisionHasMultipleTargets()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-2");
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceRevisionSnapshot(
                    "rev-2",
                    "workflow",
                    "Published",
                    "hash-2",
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    null),
            ],
            DateTimeOffset.UtcNow);
        host.ServingQueryPort.ServingSet = new ServiceServingSetSnapshot(
            "scope-a:default:default:default",
            2,
            string.Empty,
            [
                new ServiceServingTargetSnapshot(
                    "dep-paused",
                    "rev-2",
                    "def-actor-paused",
                    100,
                    ServiceServingState.Paused.ToString(),
                    []),
                new ServiceServingTargetSnapshot(
                    "dep-active",
                    "rev-2",
                    "def-actor-active",
                    5,
                    ServiceServingState.Active.ToString(),
                    []),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeBindingStatusHttpResponse>("/api/scopes/scope-a/binding");

        response.Should().NotBeNull();
        response!.Revisions.Should().ContainSingle();
        response.Revisions[0].ServingState.Should().Be(ServiceServingState.Active.ToString());
        response.Revisions[0].DeploymentId.Should().Be("dep-active");
        response.Revisions[0].PrimaryActorId.Should().Be("def-actor-active");
        response.Revisions[0].AllocationWeight.Should().Be(5);
    }

    [Fact]
    public async Task GetBindingEndpoint_ShouldReturnUnavailable_WhenScopeHasNoBinding()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeBindingStatusHttpResponse>("/api/scopes/scope-a/binding");

        response.Should().NotBeNull();
        response!.Available.Should().BeFalse();
        response.ScopeId.Should().Be("scope-a");
        response.ServiceId.Should().Be("default");
        response.Revisions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMemberPublishedServiceEndpoint_ShouldReturnStableMemberMapping()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.MemberPublishedServiceHttpResponse>(
            "/api/scopes/scope-a/members/member-a/published-service");

        response.Should().NotBeNull();
        response!.ScopeId.Should().Be("scope-a");
        response.MemberId.Should().Be("member-a");
        response.PublishedServiceId.Should().Be("member-a");
        response.PublishedServiceKey.Should().Be("scope-a:default:default:member-a");
    }

    [Fact]
    public async Task GetMemberPublishedServiceEndpoint_ShouldAllowMatchingAuthenticatedMember()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/scopes/scope-a/members/member-a/published-service");
        request.Headers.Add("X-Test-Scope-Id", "scope-a");
        request.Headers.Add("X-Test-Member-Id", "member-a");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<ScopeServiceEndpoints.MemberPublishedServiceHttpResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.ScopeId.Should().Be("scope-a");
        body.MemberId.Should().Be("member-a");
        body.PublishedServiceId.Should().Be("member-a");
    }
}
