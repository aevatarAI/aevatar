using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeServiceEndpointsTests
{
    [Fact]
    public async Task ScopeBindingEndpoint_ShouldBindWorkflowBundleToDefaultService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PutAsJsonAsync("/api/scopes/scope-a/binding", new
        {
            implementationKind = "workflow",
            displayName = "Orders App",
            workflowYamls = new[]
            {
                "name: main\nsteps:\n  - run: echo hello",
                "name: child\nsteps:\n  - run: echo child",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ScopeBindingPort.LastRequest.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest!.ScopeId.Should().Be("scope-a");
        host.ScopeBindingPort.LastRequest.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        host.ScopeBindingPort.LastRequest.Workflow.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest.Workflow!.WorkflowYamls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldBindScriptingToActiveScopeScript()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PutAsJsonAsync("/api/scopes/scope-a/binding", new
        {
            implementationKind = "scripting",
            script = new
            {
                scriptId = "script-a",
                scriptRevision = "script-rev-1",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ScopeBindingPort.LastRequest.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest!.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
        host.ScopeBindingPort.LastRequest.Script.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest.Script!.ScriptId.Should().Be("script-a");
        host.ScopeBindingPort.LastRequest.Script.ScriptRevision.Should().Be("script-rev-1");
    }

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldReturnUnauthorized_WhenAuthenticationIsMissing()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        using var request = CreateUnauthenticatedJsonRequest(
            HttpMethod.Put,
            "/api/scopes/scope-a/binding",
            new
            {
                implementationKind = "workflow",
                workflowYamls = new[]
                {
                    "name: main\nsteps:\n  - run: echo hello",
                },
            });

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.ScopeBindingPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldReturnForbidden_WhenAuthenticatedScopeClaimIsMissing()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Put,
            "/api/scopes/scope-a/binding",
            new
            {
                implementationKind = "workflow",
                workflowYamls = new[]
                {
                    "name: main\nsteps:\n  - run: echo hello",
                },
            });

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().NotBeNull();
        body!["code"].Should().Be("SCOPE_ACCESS_DENIED");
        body["message"].Should().Be("Authenticated scope is missing.");
        host.ScopeBindingPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldReturnForbidden_WhenAuthenticatedScopeClaimDoesNotMatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Put,
            "/api/scopes/scope-a/binding",
            new
            {
                implementationKind = "workflow",
                workflowYamls = new[]
                {
                    "name: main\nsteps:\n  - run: echo hello",
                },
            },
            "scope-b");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().NotBeNull();
        body!["code"].Should().Be("SCOPE_ACCESS_DENIED");
        body["message"].Should().Be("Authenticated scope does not match requested scope.");
        host.ScopeBindingPort.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldBindGAgentEndpoints()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PutAsJsonAsync("/api/scopes/scope-a/binding", new
        {
            implementationKind = "gagent",
            gagent = new
            {
                actorTypeName = "Tests.DemoActor, Tests",
                endpoints = new[]
                {
                    new
                    {
                        endpointId = "run",
                        displayName = "Run",
                        kind = "command",
                        requestTypeUrl = "type.googleapis.com/google.protobuf.StringValue",
                        responseTypeUrl = "",
                        description = "Run command",
                    },
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ScopeBindingPort.LastRequest.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest!.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        host.ScopeBindingPort.LastRequest.GAgent.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest.GAgent!.Endpoints.Should().ContainSingle();
        host.ScopeBindingPort.LastRequest.GAgent.Endpoints[0].EndpointId.Should().Be("run");
    }

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldReturnBadRequest_WhenImplementationKindIsUnsupported()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PutAsJsonAsync("/api/scopes/scope-a/binding", new
        {
            implementationKind = "unknown",
        });

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SCOPE_BINDING_REQUEST");
        body["message"].Should().Contain("Unsupported implementationKind");
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
        host.ServiceCommandPort.SetDefaultServingCommand.Should().NotBeNull();
        host.ServiceCommandPort.SetDefaultServingCommand!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "default",
        });
        host.ServiceCommandPort.SetDefaultServingCommand.RevisionId.Should().Be("rev-1");
        host.ServiceCommandPort.ActivateRevisionCommand.Should().NotBeNull();
        host.ServiceCommandPort.ActivateRevisionCommand!.RevisionId.Should().Be("rev-1");
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
        host.ServiceCommandPort.SetDefaultServingCommand.Should().BeNull();
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
            return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
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
        host.InteractionService.LastRequest.WorkflowYamls.Should().NotBeNull();
        host.InteractionService.LastRequest.WorkflowYamls.Should().HaveCount(2);
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

            return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
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
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"humanInputRequest\"");
        body.Should().Contain("aevatar.run.context");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.WorkflowYamls.Should().HaveCount(1);
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
        await host.ArtifactStore.SaveAsync(
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
            return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/invoke/chat:stream", new
        {
            prompt = "hello",
            headers = new Dictionary<string, string> { ["source"] = "tests" },
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("aevatar.run.context");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.ActorId.Should().Be("definition-actor-1");
        host.InteractionService.LastRequest.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.Metadata.Should().ContainKey("source").WhoseValue.Should().Be("tests");
    }

    [Fact]
    public async Task ScopeInvokeStreamEndpoint_ShouldReturnBadRequest_WhenTargetIsNotWorkflow()
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
        await host.ArtifactStore.SaveAsync(
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
        body["message"].Should().Contain("Only workflow services support SSE stream execution");
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
        await host.ArtifactStore.SaveAsync(
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
            return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat:stream", new
        {
            prompt = "hello orders",
            headers = new Dictionary<string, string> { ["channel"] = "tests" },
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("aevatar.run.context");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.ActorId.Should().Be("definition-actor-orders");
        host.InteractionService.LastRequest.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.Metadata.Should().ContainKey("channel").WhoseValue.Should().Be("tests");
    }

    [Fact]
    public async Task ScopeResumeRunEndpoint_ShouldResolveDefaultServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:default", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-default-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-1",
                "def-actor-1",
                "run-default-1",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-1:resume", new
        {
            stepId = "approval-1",
            approved = true,
            userInput = "approved",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ResumeDispatchService.LastCommand.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand!.ActorId.Should().Be("run-actor-default-1");
        host.ResumeDispatchService.LastCommand.RunId.Should().Be("run-default-1");
        host.ResumeDispatchService.LastCommand.StepId.Should().Be("approval-1");
    }

    [Fact]
    public async Task ScopeResumeRunEndpoint_ShouldReturnConflict_WhenRunIsAmbiguous()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
        host.LifecycleQueryPort.Deployments = new ServiceDeploymentCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceDeploymentSnapshot("dep-1", "rev-1", "def-actor-1", "Active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new ServiceDeploymentSnapshot("dep-2", "rev-2", "def-actor-2", "Inactive", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow),
            ],
            DateTimeOffset.UtcNow);
        host.RunBindingReader.BindingsByRunId["run-default-ambiguous"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-1",
                "def-actor-1",
                "run-default-ambiguous",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-2",
                "def-actor-2",
                "run-default-ambiguous",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-ambiguous:resume", new
        {
            stepId = "approval-1",
            approved = true,
        });
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        body.Should().NotBeNull();
        body!["code"].Should().Be("SERVICE_RUN_AMBIGUOUS");
    }

    [Fact]
    public async Task ScopeSignalRunEndpoint_ShouldResolveDefaultServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:default", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-default-2"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-2",
                "def-actor-1",
                "run-default-2",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-2:signal", new
        {
            signalName = "ops_window_open",
            stepId = "wait-1",
            payload = "window=open",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.SignalDispatchService.LastCommand.Should().NotBeNull();
        host.SignalDispatchService.LastCommand!.ActorId.Should().Be("run-actor-default-2");
        host.SignalDispatchService.LastCommand.RunId.Should().Be("run-default-2");
        host.SignalDispatchService.LastCommand.SignalName.Should().Be("ops_window_open");
    }

    [Fact]
    public async Task ScopeSignalRunEndpoint_ShouldHonorRequestedActorIdFilter()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
        host.LifecycleQueryPort.Deployments = new ServiceDeploymentCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceDeploymentSnapshot("dep-1", "rev-1", "def-actor-1", "Active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new ServiceDeploymentSnapshot("dep-2", "rev-2", "def-actor-2", "Inactive", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow),
            ],
            DateTimeOffset.UtcNow);
        host.RunBindingReader.BindingsByRunId["run-default-2"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-1",
                "def-actor-1",
                "run-default-2",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-2",
                "def-actor-2",
                "run-default-2",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-2:signal", new
        {
            signalName = "ops_window_open",
            actorId = "run-actor-default-2",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.SignalDispatchService.LastCommand.Should().NotBeNull();
        host.SignalDispatchService.LastCommand!.ActorId.Should().Be("run-actor-default-2");
    }

    [Fact]
    public async Task ScopeStopRunEndpoint_ShouldResolveDefaultServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:default", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-default-3"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-3",
                "def-actor-1",
                "run-default-3",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-3:stop", new
        {
            reason = "manual",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.StopDispatchService.LastCommand.Should().NotBeNull();
        host.StopDispatchService.LastCommand!.ActorId.Should().Be("run-actor-default-3");
        host.StopDispatchService.LastCommand.RunId.Should().Be("run-default-3");
        host.StopDispatchService.LastCommand.Reason.Should().Be("manual");
    }

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
    public async Task ResumeRunEndpoint_ShouldResolveRunFromServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-1",
                "def-actor-1",
                "run-1",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-1:resume", new
        {
            stepId = "approval-1",
            approved = true,
            userInput = "approved",
            metadata = new Dictionary<string, string> { ["source"] = "test" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ResumeDispatchService.LastCommand.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand!.ActorId.Should().Be("run-actor-1");
        host.ResumeDispatchService.LastCommand.RunId.Should().Be("run-1");
        host.ResumeDispatchService.LastCommand.StepId.Should().Be("approval-1");
        host.ResumeDispatchService.LastCommand.Approved.Should().BeTrue();
    }

    [Fact]
    public async Task SignalRunEndpoint_ShouldResolveRunFromServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-2"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-2",
                "def-actor-1",
                "run-2",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-2:signal", new
        {
            signalName = "ops_window_open",
            stepId = "wait-1",
            payload = "window=open",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.SignalDispatchService.LastCommand.Should().NotBeNull();
        host.SignalDispatchService.LastCommand!.ActorId.Should().Be("run-actor-2");
        host.SignalDispatchService.LastCommand.RunId.Should().Be("run-2");
        host.SignalDispatchService.LastCommand.SignalName.Should().Be("ops_window_open");
        host.SignalDispatchService.LastCommand.StepId.Should().Be("wait-1");
    }

    [Fact]
    public async Task StopRunEndpoint_ShouldResolveRunFromHistoricalDeploymentAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-active");
        host.LifecycleQueryPort.Deployments = new ServiceDeploymentCatalogSnapshot(
            "scope-a:default:default:orders",
            [
                new ServiceDeploymentSnapshot("dep-active", "rev-2", "def-actor-active", "Active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new ServiceDeploymentSnapshot("dep-old", "rev-1", "def-actor-old", "Inactive", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow),
            ],
            DateTimeOffset.UtcNow);
        host.RunBindingReader.BindingsByRunId["run-3"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-3",
                "def-actor-old",
                "run-3",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-3:stop", new
        {
            reason = "manual",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.StopDispatchService.LastCommand.Should().NotBeNull();
        host.StopDispatchService.LastCommand!.ActorId.Should().Be("run-actor-3");
        host.StopDispatchService.LastCommand.RunId.Should().Be("run-3");
        host.StopDispatchService.LastCommand.Reason.Should().Be("manual");
    }

    [Fact]
    public async Task ResumeRunEndpoint_ShouldReturnNotFound_WhenRunDoesNotBelongToService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-miss"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-x",
                "other-definition",
                "run-miss",
                "other",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-miss:resume", new
        {
            stepId = "approval-1",
            approved = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListDefaultRunsEndpoint_ShouldReturnDefaultServiceRunHistory()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-6);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-active");
        host.LifecycleQueryPort.Deployments = new ServiceDeploymentCatalogSnapshot(
            "scope-a:default:default:default",
            [
                new ServiceDeploymentSnapshot("dep-active", "rev-2", "def-actor-active", "Active", createdAt, updatedAt),
                new ServiceDeploymentSnapshot("dep-old", "rev-1", "def-actor-old", "Inactive", createdAt.AddMinutes(-10), updatedAt.AddMinutes(-10)),
            ],
            updatedAt);
        host.RunBindingReader.BindingsByRunId["run-default-list-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-list-1",
                "def-actor-old",
                "run-default-list-1",
                "default-flow",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a",
                CreatedAt: createdAt,
                UpdatedAt: updatedAt),
        ];
        host.WorkflowQueryService.SnapshotsByActorId["run-actor-default-list-1"] = new WorkflowActorSnapshot
        {
            ActorId = "run-actor-default-list-1",
            WorkflowName = "default-flow",
            CompletionStatus = WorkflowRunCompletionStatus.Running,
            StateVersion = 3,
            LastEventId = "evt-3",
            LastUpdatedAt = updatedAt,
            LastSuccess = true,
            TotalSteps = 2,
            CompletedSteps = 1,
            RoleReplyCount = 1,
            LastOutput = "working",
        };

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRunCatalogHttpResponse>("/api/scopes/scope-a/runs?take=5");

        response.Should().NotBeNull();
        response!.ServiceId.Should().Be("default");
        response.Runs.Should().ContainSingle();
        response.Runs[0].RunId.Should().Be("run-default-list-1");
        response.Runs[0].RevisionId.Should().Be("rev-1");
        response.Runs[0].DeploymentId.Should().Be("dep-old");
        response.Runs[0].WorkflowName.Should().Be("default-flow");
        host.RunBindingReader.Queries.Should().ContainSingle();
        host.RunBindingReader.Queries[0].ScopeId.Should().Be("scope-a");
    }

    [Fact]
    public async Task GetDefaultRunEndpoint_ShouldReturnScopeScopedRunSummary()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-7);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:default", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-default-detail-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-detail-1",
                "def-actor-1",
                "run-default-detail-1",
                "approval",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a",
                CreatedAt: createdAt,
                UpdatedAt: updatedAt),
        ];
        host.WorkflowQueryService.SnapshotsByActorId["run-actor-default-detail-1"] = new WorkflowActorSnapshot
        {
            ActorId = "run-actor-default-detail-1",
            WorkflowName = "approval",
            CompletionStatus = WorkflowRunCompletionStatus.Running,
            StateVersion = 4,
            LastEventId = "evt-4",
            LastUpdatedAt = updatedAt,
            LastSuccess = null,
            TotalSteps = 3,
            CompletedSteps = 2,
            RoleReplyCount = 1,
            LastOutput = "awaiting approval",
        };

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRunSummaryHttpResponse>("/api/scopes/scope-a/runs/run-default-detail-1");

        response.Should().NotBeNull();
        response!.ScopeId.Should().Be("scope-a");
        response.ServiceId.Should().Be("default");
        response.RunId.Should().Be("run-default-detail-1");
        response.ActorId.Should().Be("run-actor-default-detail-1");
        response.RevisionId.Should().Be("rev-1");
        response.WorkflowName.Should().Be("approval");
        response.StateVersion.Should().Be(4);
        response.LastEventId.Should().Be("evt-4");
    }

    [Fact]
    public async Task ListRunsEndpoint_ShouldReturnScopeScopedRunHistory()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-active");
        host.LifecycleQueryPort.Deployments = new ServiceDeploymentCatalogSnapshot(
            "scope-a:default:default:orders",
            [
                new ServiceDeploymentSnapshot("dep-active", "rev-2", "def-actor-active", "Active", createdAt, updatedAt),
                new ServiceDeploymentSnapshot("dep-old", "rev-1", "def-actor-old", "Inactive", createdAt.AddMinutes(-10), updatedAt.AddMinutes(-10)),
            ],
            updatedAt);
        host.RunBindingReader.BindingsByRunId["run-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-1",
                "def-actor-old",
                "run-1",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a",
                CreatedAt: createdAt,
                UpdatedAt: updatedAt),
        ];
        host.WorkflowQueryService.SnapshotsByActorId["run-actor-1"] = new WorkflowActorSnapshot
        {
            ActorId = "run-actor-1",
            WorkflowName = "orders",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 7,
            LastEventId = "evt-7",
            LastUpdatedAt = updatedAt,
            LastSuccess = true,
            TotalSteps = 5,
            CompletedSteps = 5,
            RoleReplyCount = 2,
            LastOutput = "done",
        };

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRunCatalogHttpResponse>("/api/scopes/scope-a/services/orders/runs?take=5");

        response.Should().NotBeNull();
        response!.Runs.Should().ContainSingle();
        response.Runs[0].RunId.Should().Be("run-1");
        response.Runs[0].RevisionId.Should().Be("rev-1");
        response.Runs[0].DeploymentId.Should().Be("dep-old");
        response.Runs[0].CompletionStatus.Should().Be(WorkflowRunCompletionStatus.Completed);
        response.Runs[0].StateVersion.Should().Be(7);
        response.Runs[0].LastEventId.Should().Be("evt-7");
        host.RunBindingReader.Queries.Should().ContainSingle();
        host.RunBindingReader.Queries[0].ScopeId.Should().Be("scope-a");
        host.RunBindingReader.Queries[0].Take.Should().Be(5);
        host.RunBindingReader.Queries[0].DefinitionActorIds.Should().BeEquivalentTo(["def-actor-active", "def-actor-old"]);
    }

    [Fact]
    public async Task GetRunEndpoint_ShouldReturnScopeScopedRunSummaryForNamedService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-7);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-orders-detail-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-orders-detail-1",
                "def-actor-1",
                "run-orders-detail-1",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a",
                CreatedAt: createdAt,
                UpdatedAt: updatedAt),
        ];
        host.WorkflowQueryService.SnapshotsByActorId["run-actor-orders-detail-1"] = new WorkflowActorSnapshot
        {
            ActorId = "run-actor-orders-detail-1",
            WorkflowName = "orders",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 8,
            LastEventId = "evt-8",
            LastUpdatedAt = updatedAt,
            LastSuccess = true,
            TotalSteps = 4,
            CompletedSteps = 4,
            RoleReplyCount = 2,
            LastOutput = "done",
        };

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRunSummaryHttpResponse>("/api/scopes/scope-a/services/orders/runs/run-orders-detail-1");

        response.Should().NotBeNull();
        response!.ScopeId.Should().Be("scope-a");
        response.ServiceId.Should().Be("orders");
        response.RunId.Should().Be("run-orders-detail-1");
        response.ActorId.Should().Be("run-actor-orders-detail-1");
        response.RevisionId.Should().Be("rev-1");
        response.WorkflowName.Should().Be("orders");
        response.StateVersion.Should().Be(8);
        response.LastEventId.Should().Be("evt-8");
    }

    [Fact]
    public async Task GetDefaultRunAuditEndpoint_ShouldReturnRunAuditReport()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-8);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        host.LifecycleQueryPort.Service = BuildService("scope-a", "default", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:default", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-audit-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-audit-1",
                "def-actor-1",
                "run-audit-1",
                "approval",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a",
                CreatedAt: createdAt,
                UpdatedAt: updatedAt),
        ];
        host.WorkflowQueryService.SnapshotsByActorId["run-actor-audit-1"] = new WorkflowActorSnapshot
        {
            ActorId = "run-actor-audit-1",
            WorkflowName = "approval",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 11,
            LastEventId = "evt-11",
            LastUpdatedAt = updatedAt,
            LastSuccess = true,
            TotalSteps = 3,
            CompletedSteps = 3,
            RoleReplyCount = 1,
            LastOutput = "approved",
        };
        host.WorkflowQueryService.ReportsByActorId["run-actor-audit-1"] = new WorkflowRunReport
        {
            WorkflowName = "approval",
            RootActorId = "run-actor-audit-1",
            CommandId = "cmd-1",
            StateVersion = 11,
            LastEventId = "evt-11",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            ProjectionScope = WorkflowRunProjectionScope.RunIsolated,
            TopologySource = WorkflowRunTopologySource.RuntimeSnapshot,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            StartedAt = createdAt,
            EndedAt = updatedAt,
            DurationMs = 1000,
            Success = true,
            FinalOutput = "approved",
            Summary = new WorkflowRunStatistics
            {
                TotalSteps = 3,
                CompletedSteps = 3,
                RoleReplyCount = 1,
            },
        };

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRunAuditHttpResponse>("/api/scopes/scope-a/runs/run-audit-1/audit");

        response.Should().NotBeNull();
        response!.Summary.RunId.Should().Be("run-audit-1");
        response.Summary.ActorId.Should().Be("run-actor-audit-1");
        response.Summary.StateVersion.Should().Be(11);
        response.Audit.RootActorId.Should().Be("run-actor-audit-1");
        response.Audit.WorkflowName.Should().Be("approval");
        response.Audit.Summary.TotalSteps.Should().Be(3);
        host.WorkflowQueryService.ReportCalls.Should().ContainSingle("run-actor-audit-1");
    }

    [Fact]
    public async Task GetRunAuditEndpoint_ShouldReturnRunAuditReportForNamedService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-8);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-orders-audit-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-orders-audit-1",
                "def-actor-1",
                "run-orders-audit-1",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a",
                CreatedAt: createdAt,
                UpdatedAt: updatedAt),
        ];
        host.WorkflowQueryService.SnapshotsByActorId["run-actor-orders-audit-1"] = new WorkflowActorSnapshot
        {
            ActorId = "run-actor-orders-audit-1",
            WorkflowName = "orders",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 12,
            LastEventId = "evt-12",
            LastUpdatedAt = updatedAt,
            LastSuccess = true,
            TotalSteps = 4,
            CompletedSteps = 4,
            RoleReplyCount = 2,
            LastOutput = "approved",
        };
        host.WorkflowQueryService.ReportsByActorId["run-actor-orders-audit-1"] = new WorkflowRunReport
        {
            WorkflowName = "orders",
            RootActorId = "run-actor-orders-audit-1",
            CommandId = "cmd-2",
            StateVersion = 12,
            LastEventId = "evt-12",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            ProjectionScope = WorkflowRunProjectionScope.RunIsolated,
            TopologySource = WorkflowRunTopologySource.RuntimeSnapshot,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            StartedAt = createdAt,
            EndedAt = updatedAt,
            DurationMs = 1000,
            Success = true,
            FinalOutput = "approved",
            Summary = new WorkflowRunStatistics
            {
                TotalSteps = 4,
                CompletedSteps = 4,
                RoleReplyCount = 2,
            },
        };

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRunAuditHttpResponse>("/api/scopes/scope-a/services/orders/runs/run-orders-audit-1/audit");

        response.Should().NotBeNull();
        response!.Summary.ServiceId.Should().Be("orders");
        response.Summary.RunId.Should().Be("run-orders-audit-1");
        response.Summary.ActorId.Should().Be("run-actor-orders-audit-1");
        response.Audit.RootActorId.Should().Be("run-actor-orders-audit-1");
        response.Audit.WorkflowName.Should().Be("orders");
        host.WorkflowQueryService.ReportCalls.Should().ContainSingle("run-actor-orders-audit-1");
    }

    private static ServiceCatalogSnapshot BuildService(string scopeId, string serviceId, string primaryActorId) =>
        new(
            $"{scopeId}:default:default:{serviceId}",
            scopeId,
            "default",
            "default",
            serviceId,
            serviceId,
            "rev-1",
            "rev-1",
            "dep-1",
            primaryActorId,
            "Active",
            [],
            [],
            DateTimeOffset.UtcNow);

    private static ServiceDeploymentCatalogSnapshot BuildDeployments(
        string serviceKey,
        string deploymentId,
        string revisionId,
        string primaryActorId) =>
        new(
            serviceKey,
            [
                new ServiceDeploymentSnapshot(
                    deploymentId,
                    revisionId,
                    primaryActorId,
                    "Active",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ],
            DateTimeOffset.UtcNow);

    private static HttpRequestMessage CreateAuthenticatedJsonRequest(
        HttpMethod method,
        string requestUri,
        object body,
        params string[] claimedScopeIds)
    {
        var request = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Test-Authenticated", "true");
        foreach (var claimedScopeId in claimedScopeIds)
        {
            request.Headers.Add("X-Test-Scope-Id", claimedScopeId);
        }

        return request;
    }

    private static HttpRequestMessage CreateUnauthenticatedJsonRequest(
        HttpMethod method,
        string requestUri,
        object body)
    {
        var request = new HttpRequestMessage(method, requestUri)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Test-Authenticated", "false");
        return request;
    }

    private sealed class ScopeServiceEndpointTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScopeServiceEndpointTestHost(
            WebApplication app,
            HttpClient client,
            RecordingServiceGovernanceCommandPort commandPort,
            RecordingServiceGovernanceQueryPort queryPort,
            RecordingScopeBindingCommandPort scopeBindingPort,
            RecordingServiceCommandPort serviceCommandPort,
            RecordingServiceInvocationPort invocationPort,
            RecordingServiceLifecycleQueryPort lifecycleQueryPort,
            RecordingServiceServingQueryPort servingQueryPort,
            FakeServiceCatalogQueryReader serviceCatalogReader,
            FakeServiceTrafficViewQueryReader trafficViewReader,
            FakeServiceRevisionArtifactStore artifactStore,
            FakeCommandInteractionService interactionService,
            FakeWorkflowExecutionQueryApplicationService workflowQueryService,
            FakeWorkflowRunBindingReader runBindingReader,
            RecordingResumeDispatchService resumeDispatchService,
            RecordingSignalDispatchService signalDispatchService,
            RecordingStopDispatchService stopDispatchService)
        {
            _app = app;
            Client = client;
            CommandPort = commandPort;
            QueryPort = queryPort;
            ScopeBindingPort = scopeBindingPort;
            ServiceCommandPort = serviceCommandPort;
            InvocationPort = invocationPort;
            LifecycleQueryPort = lifecycleQueryPort;
            ServingQueryPort = servingQueryPort;
            ServiceCatalogReader = serviceCatalogReader;
            TrafficViewReader = trafficViewReader;
            ArtifactStore = artifactStore;
            InteractionService = interactionService;
            WorkflowQueryService = workflowQueryService;
            RunBindingReader = runBindingReader;
            ResumeDispatchService = resumeDispatchService;
            SignalDispatchService = signalDispatchService;
            StopDispatchService = stopDispatchService;
        }

        public HttpClient Client { get; }

        public RecordingServiceGovernanceCommandPort CommandPort { get; }

        public RecordingServiceGovernanceQueryPort QueryPort { get; }

        public RecordingScopeBindingCommandPort ScopeBindingPort { get; }

        public RecordingServiceCommandPort ServiceCommandPort { get; }

        public RecordingServiceInvocationPort InvocationPort { get; }

        public RecordingServiceLifecycleQueryPort LifecycleQueryPort { get; }

        public RecordingServiceServingQueryPort ServingQueryPort { get; }

        public FakeServiceCatalogQueryReader ServiceCatalogReader { get; }

        public FakeServiceTrafficViewQueryReader TrafficViewReader { get; }

        public FakeServiceRevisionArtifactStore ArtifactStore { get; }

        public FakeCommandInteractionService InteractionService { get; }

        public FakeWorkflowExecutionQueryApplicationService WorkflowQueryService { get; }

        public FakeWorkflowRunBindingReader RunBindingReader { get; }

        public RecordingResumeDispatchService ResumeDispatchService { get; }

        public RecordingSignalDispatchService SignalDispatchService { get; }

        public RecordingStopDispatchService StopDispatchService { get; }

        public static async Task<ScopeServiceEndpointTestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var commandPort = new RecordingServiceGovernanceCommandPort();
            var queryPort = new RecordingServiceGovernanceQueryPort();
            var scopeBindingPort = new RecordingScopeBindingCommandPort();
            var serviceCommandPort = new RecordingServiceCommandPort();
            var invocationPort = new RecordingServiceInvocationPort();
            var lifecycleQueryPort = new RecordingServiceLifecycleQueryPort();
            var servingQueryPort = new RecordingServiceServingQueryPort();
            var serviceCatalogReader = new FakeServiceCatalogQueryReader();
            var trafficViewReader = new FakeServiceTrafficViewQueryReader();
            var artifactStore = new FakeServiceRevisionArtifactStore();
            var interactionService = new FakeCommandInteractionService();
            var workflowQueryService = new FakeWorkflowExecutionQueryApplicationService();
            var runBindingReader = new FakeWorkflowRunBindingReader();
            var resumeDispatchService = new RecordingResumeDispatchService();
            var signalDispatchService = new RecordingSignalDispatchService();
            var stopDispatchService = new RecordingStopDispatchService();
            builder.Services.AddSingleton<IServiceGovernanceCommandPort>(commandPort);
            builder.Services.AddSingleton<IServiceGovernanceQueryPort>(queryPort);
            builder.Services.AddSingleton<IScopeBindingCommandPort>(scopeBindingPort);
            builder.Services.AddSingleton<IServiceCommandPort>(serviceCommandPort);
            builder.Services.AddSingleton<IServiceInvocationPort>(invocationPort);
            builder.Services.AddSingleton<IServiceLifecycleQueryPort>(lifecycleQueryPort);
            builder.Services.AddSingleton<IServiceServingQueryPort>(servingQueryPort);
            builder.Services.AddSingleton<IServiceCatalogQueryReader>(serviceCatalogReader);
            builder.Services.AddSingleton<IServiceTrafficViewQueryReader>(trafficViewReader);
            builder.Services.AddSingleton<IServiceRevisionArtifactStore>(artifactStore);
            builder.Services.AddSingleton<ServiceInvocationResolutionService>();
            builder.Services.AddSingleton<IInvokeAdmissionAuthorizer, AllowAllInvokeAdmissionAuthorizer>();
            builder.Services.AddSingleton<ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>>(interactionService);
            builder.Services.AddSingleton<IWorkflowExecutionQueryApplicationService>(workflowQueryService);
            builder.Services.AddSingleton<IWorkflowRunBindingReader>(runBindingReader);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(resumeDispatchService);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(signalDispatchService);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(stopDispatchService);
            builder.Services.AddSingleton<IOptions<ScopeWorkflowCapabilityOptions>>(
                Options.Create(new ScopeWorkflowCapabilityOptions
                {
                    DefaultServiceId = "default",
                    ServiceAppId = "default",
                    ServiceNamespace = "default",
                }));
            builder.Services.AddAuthentication("Test")
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.UseAuthentication();
            app.Use(async (http, next) =>
            {
                var hasExplicitAuthenticationHeader = http.Request.Headers.TryGetValue("X-Test-Authenticated", out var authenticatedValues);
                var shouldAuthenticate = !hasExplicitAuthenticationHeader ||
                    (bool.TryParse(authenticatedValues, out var authenticated) && authenticated);
                if (shouldAuthenticate)
                {
                    var claims = new List<Claim>();
                    if (http.Request.Headers.TryGetValue("X-Test-Scope-Id", out var claimedScopeValues))
                    {
                        var claimedScopeIds = claimedScopeValues
                            .ToString()
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var claimedScopeId in claimedScopeIds)
                        {
                            claims.Add(new Claim(WorkflowRunCommandMetadataKeys.ScopeId, claimedScopeId));
                        }
                    }
                    else if (!hasExplicitAuthenticationHeader &&
                        TryGetRequestedScopeId(http.Request.Path.Value, out var requestedScopeId))
                    {
                        claims.Add(new Claim(WorkflowRunCommandMetadataKeys.ScopeId, requestedScopeId));
                    }

                    http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
                }

                await next();
            });
            app.UseAuthorization();
            app.MapScopeServiceEndpoints();
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new ScopeServiceEndpointTestHost(
                app,
                client,
                commandPort,
                queryPort,
                scopeBindingPort,
                serviceCommandPort,
                invocationPort,
                lifecycleQueryPort,
                servingQueryPort,
                serviceCatalogReader,
                trafficViewReader,
                artifactStore,
                interactionService,
                workflowQueryService,
                runBindingReader,
                resumeDispatchService,
                signalDispatchService,
                stopDispatchService);
        }

        private static bool TryGetRequestedScopeId(string? path, out string scopeId)
        {
            var segments = path?
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments is { Length: >= 3 } &&
                string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(segments[1], "scopes", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(segments[2]))
            {
                scopeId = segments[2];
                return true;
            }

            scopeId = string.Empty;
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class RecordingScopeBindingCommandPort : IScopeBindingCommandPort
    {
        public ScopeBindingUpsertRequest? LastRequest { get; private set; }

        public Task<ScopeBindingUpsertResult> UpsertAsync(ScopeBindingUpsertRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ScopeBindingUpsertResult(
                request.ScopeId,
                "default",
                request.DisplayName?.Trim() ?? "main",
                request.RevisionId?.Trim() ?? "rev-1",
                request.ImplementationKind,
                "scope-binding:expected-actor",
                WorkflowName: request.Workflow?.WorkflowYamls.FirstOrDefault() is { } firstWorkflowYaml && firstWorkflowYaml.Contains("name:", StringComparison.Ordinal)
                    ? "main"
                    : string.Empty,
                DefinitionActorIdPrefix: request.ImplementationKind == ScopeBindingImplementationKind.Workflow
                    ? "scope-workflow:scope-a:default"
                    : string.Empty,
                Workflow: request.ImplementationKind == ScopeBindingImplementationKind.Workflow
                    ? new ScopeBindingWorkflowResult("main", "scope-workflow:scope-a:default")
                    : null,
                Script: request.Script == null
                    ? null
                    : new ScopeBindingScriptResult(
                        request.Script.ScriptId,
                        request.Script.ScriptRevision ?? "script-rev-1",
                        "definition-script-1"),
                GAgent: request.GAgent == null
                    ? null
                    : new ScopeBindingGAgentResult(
                        request.GAgent.ActorTypeName)));
        }
    }

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        public RetireServiceRevisionCommand? RetireRevisionCommand { get; private set; }

        public SetDefaultServingRevisionCommand? SetDefaultServingCommand { get; private set; }

        public ActivateServiceRevisionCommand? ActivateRevisionCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            SetDefaultServingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("service-actor", "cmd-default-serving", "corr-default-serving"));
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            ActivateRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("service-actor", "cmd-activate", "corr-activate"));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default)
        {
            RetireRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("service-actor", "cmd-retire", "corr-retire"));
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingServiceGovernanceCommandPort : IServiceGovernanceCommandPort
    {
        public CreateServiceBindingCommand? CreateBindingCommand { get; private set; }

        public UpdateServiceBindingCommand? UpdateBindingCommand { get; private set; }

        public RetireServiceBindingCommand? RetireBindingCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(CreateServiceBindingCommand command, CancellationToken ct = default)
        {
            CreateBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-create-binding", "corr-create-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(UpdateServiceBindingCommand command, CancellationToken ct = default)
        {
            UpdateBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-update-binding", "corr-update-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(RetireServiceBindingCommand command, CancellationToken ct = default)
        {
            RetireBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-retire-binding", "corr-retire-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(CreateServiceEndpointCatalogCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(UpdateServiceEndpointCatalogCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(CreateServicePolicyCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(UpdateServicePolicyCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(RetireServicePolicyCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingServiceGovernanceQueryPort : IServiceGovernanceQueryPort
    {
        public ServiceIdentity? LastBindingsIdentity { get; private set; }

        public ServiceBindingCatalogSnapshot? BindingsResult { get; set; }

        public Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastBindingsIdentity = identity;
            return Task.FromResult(BindingsResult);
        }

        public Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingServiceInvocationPort : IServiceInvocationPort
    {
        public ServiceInvocationRequest? LastRequest { get; private set; }

        public Func<ServiceInvocationRequest, Exception?>? ExceptionFactory { get; set; }

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ServiceInvocationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            var exception = ExceptionFactory?.Invoke(request);
            if (exception != null)
                throw exception;
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                DeploymentId = "dep-1",
                TargetActorId = "actor-1",
                CommandId = "cmd-1",
                CorrelationId = "corr-1",
            });
        }
    }

    private sealed class RecordingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public ServiceCatalogSnapshot? Service { get; set; }

        public ServiceRevisionCatalogSnapshot? Revisions { get; set; }

        public ServiceDeploymentCatalogSnapshot? Deployments { get; set; }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Service);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Revisions);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Deployments);
    }

    private sealed class RecordingServiceServingQueryPort : IServiceServingQueryPort
    {
        public ServiceServingSetSnapshot? ServingSet { get; set; }

        public Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(ServingSet);

        public Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeServiceCatalogQueryReader : IServiceCatalogQueryReader
    {
        public ServiceCatalogSnapshot? Service { get; set; }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Service);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(Service == null ? [] : [Service]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(Service == null ? [] : [Service]);
    }

    private sealed class FakeServiceTrafficViewQueryReader : IServiceTrafficViewQueryReader
    {
        public ServiceTrafficViewSnapshot? View { get; set; }

        public Task<ServiceTrafficViewSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(View);
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

    private sealed class FakeWorkflowRunBindingReader : IWorkflowRunBindingReader
    {
        public Dictionary<string, IReadOnlyList<WorkflowActorBinding>> BindingsByRunId { get; } =
            new(StringComparer.Ordinal);

        public List<WorkflowRunBindingQuery> Queries { get; } = [];

        public Task<IReadOnlyList<WorkflowActorBinding>> ListByRunIdAsync(
            string runId,
            int take = 20,
            CancellationToken ct = default)
        {
            BindingsByRunId.TryGetValue(runId, out var bindings);
            return Task.FromResult<IReadOnlyList<WorkflowActorBinding>>(bindings ?? []);
        }

        public Task<IReadOnlyList<WorkflowActorBinding>> QueryAsync(
            WorkflowRunBindingQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            var definitionActorIds = new HashSet<string>(query.DefinitionActorIds, StringComparer.Ordinal);
            var bindings = BindingsByRunId.Values
                .SelectMany(x => x)
                .Where(x => x.ActorKind == WorkflowActorKind.Run)
                .Where(x => string.IsNullOrWhiteSpace(query.ScopeId) || string.Equals(x.ScopeId, query.ScopeId, StringComparison.Ordinal))
                .Where(x => definitionActorIds.Count == 0 || definitionActorIds.Contains(x.EffectiveDefinitionActorId))
                .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.ActorId, StringComparer.Ordinal)
                .Take(query.Take)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowActorBinding>>(bindings);
        }
    }

    private sealed class FakeWorkflowExecutionQueryApplicationService : IWorkflowExecutionQueryApplicationService
    {
        public bool ActorQueryEnabled => true;

        public Dictionary<string, WorkflowActorSnapshot> SnapshotsByActorId { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorkflowRunReport> ReportsByActorId { get; } = new(StringComparer.Ordinal);

        public List<string> SnapshotCalls { get; } = [];

        public List<string> ReportCalls { get; } = [];

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowAgentSummary>>([]);

        public IReadOnlyList<string> ListWorkflows() => [];

        public IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog() => [];

        public WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName) => null;

        public WorkflowCapabilitiesDocument GetCapabilities() => new();

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            SnapshotCalls.Add(actorId);
            SnapshotsByActorId.TryGetValue(actorId, out var snapshot);
            return Task.FromResult<WorkflowActorSnapshot?>(snapshot);
        }

        public Task<WorkflowRunReport?> GetActorReportAsync(string actorId, CancellationToken ct = default)
        {
            ReportCalls.Add(actorId);
            ReportsByActorId.TryGetValue(actorId, out var report);
            return Task.FromResult<WorkflowRunReport?>(report);
        }

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorTimelineItem>>([]);

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> ListActorGraphEdgesAsync(string actorId, int take = 200, WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowActorGraphEdge>>([]);

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(string actorId, int depth = 2, int take = 200, WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowActorGraphSubgraph());
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

    private sealed class AllowAllInvokeAdmissionAuthorizer : IInvokeAdmissionAuthorizer
    {
        public Task AuthorizeAsync(
            string serviceKey,
            string deploymentId,
            PreparedServiceRevisionArtifact artifact,
            ServiceEndpointDescriptor endpoint,
            ServiceInvocationRequest request,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingResumeDispatchService
        : ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowResumeCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowResumeCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-resume", "corr-resume")));
        }
    }

    private sealed class RecordingSignalDispatchService
        : ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowSignalCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowSignalCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-signal", "corr-signal")));
        }
    }

    private sealed class RecordingStopDispatchService
        : ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowStopCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowStopCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-stop", "corr-stop")));
        }
    }

    private sealed class TestAuthHandler
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory logger,
            System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            // The custom middleware after UseAuthentication() overrides http.User.
            // This handler returns NoResult so it does not interfere.
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
        }
    }
}
