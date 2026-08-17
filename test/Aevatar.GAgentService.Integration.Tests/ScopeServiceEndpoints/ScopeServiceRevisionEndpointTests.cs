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
public sealed class ScopeServiceRevisionEndpointTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task ActivateBindingRevisionEndpoint_ShouldPromoteHistoricalRevisionOnDefaultService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
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
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/binding/revisions/rev-1:activate", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ServiceCommandPort.ActivateRevisionCommand.Should().NotBeNull();
        host.ServiceCommandPort.ActivateRevisionCommand!.RevisionId.Should().Be("rev-1");
        host.ServiceCommandPort.ActivateRevisionCommand.ExpectedArtifactHash.Should().Be("hash-1");
    }

    [Fact]
    public async Task ActivateBindingRevisionEndpoint_ShouldReturnNotFound_WhenScopeHasNoBinding()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/binding/revisions/rev-1:activate", new { });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotBeNull();
        body!["code"].Should().Be("SCOPE_BINDING_NOT_FOUND");
    }

    [Fact]
    public async Task ActivateBindingRevisionEndpoint_ShouldReturnNotFound_WhenRevisionDoesNotExist()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
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
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/binding/revisions/rev-missing:activate", new { });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotBeNull();
        body!["code"].Should().Be("SCOPE_BINDING_REVISION_NOT_FOUND");
    }

    [Fact]
    public async Task ActivateBindingRevisionEndpoint_ShouldRejectRetiredRevision()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceRevisionSnapshot(
                    "rev-1",
                    "workflow",
                    ServiceRevisionStatus.Retired.ToString(),
                    "hash-1",
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-1)),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/binding/revisions/rev-1:activate", new { });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("SCOPE_BINDING_REVISION_RETIRED");
        host.ServiceCommandPort.ActivateRevisionCommand.Should().BeNull();
    }

    [Fact]
    public async Task GetDefaultServiceRevisionsEndpoint_ShouldReturnVersionWatermarkAndTypedGovernance()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            "scope-a",
            "default",
            "default",
            "default",
            "Orders App",
            "rev-workflow",
            "rev-workflow",
            "dep-2",
            "workflow-def-1",
            "Active",
            [],
            [],
            DateTimeOffset.UtcNow);
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceRevisionSnapshot(
                    "rev-static",
                    "static",
                    "Published",
                    "hash-static",
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    null,
                    new ServiceRevisionImplementationSnapshot(
                        new ServiceRevisionStaticSnapshot("Tests.StaticActor, Tests", "static-actor-1"))),
                new ServiceRevisionSnapshot(
                    "rev-workflow",
                    "workflow",
                    "Published",
                    "hash-workflow",
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    null,
                    new ServiceRevisionImplementationSnapshot(
                        Workflow: new ServiceRevisionWorkflowSnapshot("approval", "workflow-def-1", 2))),
            ],
            DateTimeOffset.UtcNow,
            9,
            "evt-9");
        host.ServingQueryPort.ServingSet = new ServiceServingSetSnapshot(
            "scope-a:default:default:default",
            9,
            string.Empty,
            [
                new ServiceServingTargetSnapshot(
                    "dep-2",
                    "rev-workflow",
                    "workflow-def-1",
                    100,
                    ServiceServingState.Active.ToString(),
                    []),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRevisionCatalogHttpResponse>("/api/scopes/scope-a/revisions");

        response.Should().NotBeNull();
        response!.CatalogStateVersion.Should().Be(9);
        response.CatalogLastEventId.Should().Be("evt-9");
        response.Revisions.Single(x => x.RevisionId == "rev-workflow").WorkflowName.Should().Be("approval");
        response.Revisions.Single(x => x.RevisionId == "rev-workflow").WorkflowDefinitionActorId.Should().Be("workflow-def-1");
        response.Revisions.Single(x => x.RevisionId == "rev-workflow").InlineWorkflowCount.Should().Be(2);
        response.Revisions.Single(x => x.RevisionId == "rev-static").StaticActorTypeName.Should().Be("Tests.StaticActor, Tests");
    }

    [Fact]
    public async Task GetDefaultServiceRevisionEndpoint_ShouldReturnTypedRevision()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = new ServiceCatalogSnapshot(
            "scope-a:default:default:default",
            "scope-a",
            "default",
            "default",
            "default",
            "Orders App",
            "rev-workflow",
            "rev-workflow",
            "dep-2",
            "workflow-def-1",
            "Active",
            [],
            [],
            DateTimeOffset.UtcNow);
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceRevisionSnapshot(
                    "rev-workflow",
                    "workflow",
                    "Published",
                    "hash-workflow",
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddHours(-1),
                    null,
                    new ServiceRevisionImplementationSnapshot(
                        Workflow: new ServiceRevisionWorkflowSnapshot("approval", "workflow-def-1", 2))),
            ],
            DateTimeOffset.UtcNow,
            9,
            "evt-9");
        host.ServingQueryPort.ServingSet = new ServiceServingSetSnapshot(
            "scope-a:default:default:default",
            9,
            string.Empty,
            [
                new ServiceServingTargetSnapshot(
                    "dep-2",
                    "rev-workflow",
                    "workflow-def-1",
                    100,
                    ServiceServingState.Active.ToString(),
                    []),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeBindingRevisionHttpResponse>("/api/scopes/scope-a/revisions/rev-workflow");

        response.Should().NotBeNull();
        response!.RevisionId.Should().Be("rev-workflow");
        response.WorkflowName.Should().Be("approval");
        response.WorkflowDefinitionActorId.Should().Be("workflow-def-1");
        response.InlineWorkflowCount.Should().Be(2);
        response.IsDefaultServing.Should().BeTrue();
        response.IsActiveServing.Should().BeTrue();
        response.DeploymentId.Should().Be("dep-2");
    }

    [Fact]
    public async Task GetServiceRevisionEndpoint_ShouldReturnTypedRevisionForNamedService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "static-actor-1");
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:orders",
            [
                new ServiceRevisionSnapshot(
                    "rev-static",
                    "static",
                    "Published",
                    "hash-static",
                    string.Empty,
                    [],
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    null,
                    new ServiceRevisionImplementationSnapshot(
                        new ServiceRevisionStaticSnapshot("Tests.StaticActor, Tests", "static-actor-1"))),
            ],
            DateTimeOffset.UtcNow,
            3,
            "evt-3");
        host.ServingQueryPort.ServingSet = new ServiceServingSetSnapshot(
            "scope-a:default:default:orders",
            3,
            string.Empty,
            [
                new ServiceServingTargetSnapshot(
                    "dep-static",
                    "rev-static",
                    "static-actor-1",
                    100,
                    ServiceServingState.Active.ToString(),
                    []),
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeBindingRevisionHttpResponse>("/api/scopes/scope-a/services/orders/revisions/rev-static");

        response.Should().NotBeNull();
        response!.RevisionId.Should().Be("rev-static");
        response.StaticActorTypeName.Should().Be("Tests.StaticActor, Tests");
        response.IsServingTarget.Should().BeTrue();
        response.ServingState.Should().Be(ServiceServingState.Active.ToString());
    }

    [Fact]
    public async Task RetireBindingRevisionEndpoint_ShouldDispatchRetireRevisionForDefaultService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
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
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/binding/revisions/rev-1:retire", new { });
        var body = await response.Content.ReadFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRevisionActionHttpResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.RevisionId.Should().Be("rev-1");
        body.Status.Should().Be("retired");
        host.ServiceCommandPort.RetireRevisionCommand.Should().NotBeNull();
        host.ServiceCommandPort.RetireRevisionCommand!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "default",
        });
        host.ServiceCommandPort.RetireRevisionCommand.RevisionId.Should().Be("rev-1");
    }

    [Fact]
    public async Task RetireServiceRevisionEndpoint_ShouldDispatchRetireRevisionForNamedService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Revisions = new ServiceRevisionCatalogSnapshot(
            "scope-a:default:default:orders",
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
            ],
            DateTimeOffset.UtcNow);

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/revisions/rev-1:retire", new { });
        var body = await response.Content.ReadFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRevisionActionHttpResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.ServiceId.Should().Be("orders");
        body.RevisionId.Should().Be("rev-1");
        body.Status.Should().Be("retired");
        host.ServiceCommandPort.RetireRevisionCommand.Should().NotBeNull();
        host.ServiceCommandPort.RetireRevisionCommand!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "orders",
        });
    }
}
