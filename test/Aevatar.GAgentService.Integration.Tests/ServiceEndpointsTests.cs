using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Governance.Hosting.Identity;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ServiceEndpointsTests
{
    [Fact]
    public async Task CreateServiceAsync_ShouldDispatchTypedCommand()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/", new ServiceEndpoints.CreateServiceHttpRequest(
            " tenant ",
            " app ",
            " ns ",
            " service-a ",
            "Orders",
            [
                new ServiceEndpoints.ServiceEndpointHttpRequest(
                    " submit ",
                    "Submit",
                    "unexpected-kind",
                    "type.googleapis.com/demo.Submit",
                    string.Empty,
                    "submit command"),
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateServiceCommand.Should().NotBeNull();
        host.CommandPort.CreateServiceCommand!.Spec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "service-a",
        });
        host.CommandPort.CreateServiceCommand.Spec.Endpoints.Should().ContainSingle();
        host.CommandPort.CreateServiceCommand.Spec.Endpoints[0].Kind.Should().Be(ServiceEndpointKind.Command);
        host.CommandPort.CreateServiceCommand.Spec.ExternalExposure.Should().BeNull();
    }

    [Fact]
    public async Task UpdateExternalExposureAsync_ShouldReturnNotFound()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PutAsJsonAsync("/api/services/orders/external-exposure", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            nyxidSlug = "aevatar-orders",
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.CommandPort.ReconcileExternalExposureCommand.Should().BeNull();
    }

    [Fact]
    public async Task CreateServiceAsync_ShouldIgnoreInlineExternalExposureJson()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/", new
        {
            tenantId = "tenant",
            appId = "app",
            @namespace = "ns",
            serviceId = "orders",
            displayName = "Orders",
            endpoints = new[]
            {
                new
                {
                    endpointId = "submit",
                    displayName = "Submit",
                    kind = "command",
                    requestTypeUrl = "type.googleapis.com/demo.Submit",
                    responseTypeUrl = "",
                    description = "submit command",
                },
            },
            externalExposure = new
            {
                nyxidSlug = "aevatar-orders",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateServiceCommand.Should().NotBeNull();
        host.CommandPort.CreateServiceCommand!.Spec.ExternalExposure.Should().BeNull();
    }

    [Fact]
    public async Task CreateServiceAsync_WhenAuthenticatedIdentityConflictsWithBody_ShouldUseClaimIdentity()
    {
        await using var host = await EndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/")
        {
            Content = JsonContent.Create(new ServiceEndpoints.CreateServiceHttpRequest(
                "spoof-tenant",
                "spoof-app",
                "spoof-ns",
                "service-a",
                "Orders",
                [])),
        };
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-claim");
        request.Headers.Add("X-Test-App-Id", "app-claim");
        request.Headers.Add("X-Test-Namespace", "ns-claim");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateServiceCommand!.Spec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant-claim",
            AppId = "app-claim",
            Namespace = "ns-claim",
            ServiceId = "service-a",
        });
    }

    [Fact]
    public async Task CreateServiceAsync_WhenAuthenticatedIdentityMissingClaims_ShouldReturnForbidden()
    {
        await using var host = await EndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/")
        {
            Content = JsonContent.Create(new ServiceEndpoints.CreateServiceHttpRequest(
                "tenant",
                "app",
                "ns",
                "service-a",
                "Orders",
                [])),
        };
        request.Headers.Add("X-Test-Authenticated", "true");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.CommandPort.CreateServiceCommand.Should().BeNull();
    }

    [Fact]
    public async Task CreateRevisionAsync_ShouldMapStaticImplementation()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-1",
            "static",
            new ServiceEndpoints.StaticRevisionHttpRequest(
                typeof(TestStaticAgent).AssemblyQualifiedName!,
                "static-actor",
                [
                    new ServiceEndpoints.ServiceEndpointHttpRequest(
                        "submit",
                        "Submit",
                        "command",
                        "type.googleapis.com/demo.Submit",
                        string.Empty,
                        "submit command"),
                ]),
            null,
            null));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand!.Spec.ImplementationKind.Should().Be(ServiceImplementationKind.Static);
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.ActorTypeName.Should().Be(typeof(TestStaticAgent).AssemblyQualifiedName);
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.PreferredActorId.Should().Be("static-actor");
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.Endpoints.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateRevisionAsync_ShouldMapScriptingAndWorkflowImplementations()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var scriptingResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-script",
            "scripting",
            null,
            new ServiceEndpoints.ScriptingRevisionHttpRequest("script-a", "7", "script-definition"),
            null));
        scriptingResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.ScriptingSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.ScriptingSpec.ScriptId.Should().Be("script-a");
        host.CommandPort.CreateRevisionCommand.Spec.ScriptingSpec.DefinitionActorId.Should().Be("script-definition");

        var workflowResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-workflow",
            "workflow",
            null,
            null,
            new ServiceEndpoints.WorkflowRevisionHttpRequest(
                "approval",
                "name: approval",
                "workflow-definition",
                new Dictionary<string, string> { ["child.yaml"] = "name: child" })));

        workflowResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.WorkflowSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.WorkflowName.Should().Be("approval");
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.InlineWorkflowYamls.Should().ContainKey("child.yaml");
    }

    [Fact]
    public async Task CreateRevisionAsync_WhenWorkflowAdmissionFails_ShouldNotDispatchRevisionCommand()
    {
        await using var host = await EndpointTestHost.StartAsync(
            new InvalidOperationException("external capability is not ready"));

        var response = await host.Client.PostAsJsonAsync(
            "/api/services/orders/revisions",
            new ServiceEndpoints.CreateRevisionHttpRequest(
                "tenant",
                "app",
                "ns",
                "rev-workflow",
                "workflow",
                null,
                null,
                new ServiceEndpoints.WorkflowRevisionHttpRequest(
                    "approval",
                    "name: approval",
                    "workflow-definition",
                    new Dictionary<string, string>())));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        host.CapabilityAdmission.Requests.Should().ContainSingle();
        host.CommandPort.CreateRevisionCommand.Should().BeNull();
    }

    [Fact]
    public async Task CreateRevisionAsync_WhenExplicitRequestAdmissionFails_ShouldReturnBadRequestWithoutDispatch()
    {
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
            Code = "NYXID_EXPLICIT_REQUEST_CONFIRMATION_STALE",
            SafeMessage = "Explicit request confirmation no longer matches the current contract.",
        });
        await using var host = await EndpointTestHost.StartAsync(
            new WorkflowExternalCapabilityAdmissionException(readiness));

        var response = await host.Client.PostAsJsonAsync(
            "/api/services/svc-alpha/revisions",
            new ServiceEndpoints.CreateRevisionHttpRequest(
                "tenant-alpha",
                "app-alpha",
                "ns-alpha",
                "rev-alpha",
                "workflow",
                null,
                null,
                new ServiceEndpoints.WorkflowRevisionHttpRequest(
                    "wf-alpha",
                    "name: wf-alpha",
                    "workflow-definition-alpha",
                    null),
                [new NyxIdExplicitRequestConfirmationInput(
                    "wf-alpha/request-alpha",
                    "stale-digest",
                    "read_only")]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.CapabilityAdmission.Requests.Should().ContainSingle();
        host.CommandPort.CreateRevisionCommand.Should().BeNull();
    }

    [Fact]
    public async Task CreateRevisionAsync_ForWorkflow_ShouldAdmitExactBundleWithTransientHttpAuthority()
    {
        const string publishedServiceId = "svc-published-alpha";
        const string workflowId = "wf-draft-alpha";
        const string revisionId = "rev-alpha";
        new[] { publishedServiceId, workflowId, revisionId }
            .Distinct(StringComparer.Ordinal).Should().HaveCount(3);
        await using var host = await EndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/services/{publishedServiceId}/revisions")
        {
            Content = JsonContent.Create(new ServiceEndpoints.CreateRevisionHttpRequest(
                "spoof-tenant",
                "spoof-app",
                "spoof-ns",
                revisionId,
                "workflow",
                null,
                null,
                new ServiceEndpoints.WorkflowRevisionHttpRequest(
                    "approval",
                    "name: approval",
                    "workflow-definition",
                    new Dictionary<string, string>
                    {
                        ["child"] = "name: child",
                    },
                    workflowId))),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            "runtime-caller-credential");
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-claim");
        request.Headers.Add("X-Test-App-Id", "app-claim");
        request.Headers.Add("X-Test-Namespace", "ns-claim");
        request.Headers.Add("X-Test-Caller-Id", "caller-alpha");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var admissionRequest = host.CapabilityAdmission.Requests.Should().ContainSingle().Which;
        admissionRequest.Access.ScopeId.Should().Be("tenant-claim");
        admissionRequest.Access.CallerId.Should().Be("caller-alpha");
        admissionRequest.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("runtime-caller-credential");
        admissionRequest.WorkflowYaml.Should().Be("name: approval");
        admissionRequest.InlineWorkflowYamls.Should().Contain("child", "name: child");
        admissionRequest.SourceKind.Should().Be("service_revision");
        admissionRequest.ExecutionMode.Should().Be(ExternalCapabilityExecutionMode.Durable);
        admissionRequest.WorkflowId.Should().Be(workflowId);
        admissionRequest.RevisionId.Should().Be(revisionId);
        host.CommandPort.CreateRevisionCommand!.Spec.WorkflowSpec.CapabilityAdmissionPlan.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.WorkflowId.Should().Be(workflowId);
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.CapabilityAdmissionPlan.ExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Durable);
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.ExpectedExecutionMode.Should()
            .Be(ExternalCapabilityExecutionMode.Durable);
    }

    [Fact]
    public async Task CreateRevisionAsync_ForWorkflow_ShouldMapExplicitRequestConfirmationWithoutCallerSuppliedGrantor()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/svc-alpha/revisions")
        {
            Content = JsonContent.Create(new ServiceEndpoints.CreateRevisionHttpRequest(
                "spoof-tenant",
                "spoof-app",
                "spoof-ns",
                "rev-alpha",
                "workflow",
                null,
                null,
                new ServiceEndpoints.WorkflowRevisionHttpRequest(
                    "wf-alpha",
                    "name: wf-alpha",
                    "workflow-definition-alpha",
                    null),
                [
                    new NyxIdExplicitRequestConfirmationInput(
                        "wf-alpha/request-alpha",
                        "digest-alpha",
                        "read_only"),
                ])),
        };
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-claim");
        request.Headers.Add("X-Test-App-Id", "app-claim");
        request.Headers.Add("X-Test-Namespace", "ns-claim");
        request.Headers.Add("X-Test-Caller-Id", "authenticated-owner-alpha");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var admission = host.CapabilityAdmission.Requests.Should().ContainSingle().Which;
        admission.Access.CallerId.Should().Be("authenticated-owner-alpha");
        admission.ExplicitRequestConfirmations.Should().ContainSingle().Which.Should()
            .BeEquivalentTo(new NyxIdExplicitRequestConfirmation
            {
                CallSiteId = "wf-alpha/request-alpha",
                RequestContractDigest = "digest-alpha",
                AttestedRisk = NyxIdOperationRisk.ReadOnly,
            });
        host.CommandPort.CreateRevisionCommand.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand!.ToString().Should().NotContain("attested_risk");
    }

    [Fact]
    public async Task CreateRevisionAsync_WithNullExplicitRequestConfirmation_ShouldReturnTypedBadRequestWithoutAdmissionOrDispatch()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync(
            "/api/services/svc-alpha/revisions",
            new ServiceEndpoints.CreateRevisionHttpRequest(
                "tenant-alpha",
                "app-alpha",
                "ns-alpha",
                "rev-alpha",
                "workflow",
                null,
                null,
                new ServiceEndpoints.WorkflowRevisionHttpRequest(
                    "wf-alpha",
                    "name: wf-alpha",
                    "workflow-definition-alpha",
                    null),
                [null!]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should()
            .Contain("INVALID_EXPLICIT_REQUEST_CONFIRMATION");
        host.CapabilityAdmission.Requests.Should().BeEmpty();
        host.CommandPort.CreateRevisionCommand.Should().BeNull();
    }

    [Fact]
    public async Task CreateRevisionAsync_WithMalformedAuthorizationAndDelegation_ShouldRejectBeforeAdmissionOrDispatch()
    {
        await using var host = await EndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/svc-alpha/revisions")
        {
            Content = JsonContent.Create(new ServiceEndpoints.CreateRevisionHttpRequest(
                "tenant-alpha",
                "app-alpha",
                "ns-alpha",
                "rev-alpha",
                "workflow",
                null,
                null,
                new ServiceEndpoints.WorkflowRevisionHttpRequest(
                    "wf-alpha",
                    "name: wf-alpha\nsteps: []\n",
                    "definition-alpha",
                    null))),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer token with spaces");
        request.Headers.TryAddWithoutValidation("X-NyxID-Delegation-Token", "delegation-token");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().Contain("INVALID_WORKFLOW_CALLER_CREDENTIAL");
        body.Should().NotContain("token with spaces");
        body.Should().NotContain("delegation-token");
        host.CapabilityAdmission.Requests.Should().BeEmpty();
        host.CommandPort.CreateRevisionCommand.Should().BeNull();
    }

    [Fact]
    public async Task RevisionLifecycleEndpoints_ShouldDispatchCommandPortCalls()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var prepareResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/revisions/rev-1:prepare",
            new ServiceEndpoints.ServiceIdentityHttpRequest("tenant", "app", "ns"));
        var publishResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/revisions/rev-1:publish",
            new ServiceEndpoints.ServiceIdentityHttpRequest("tenant", "app", "ns"));
        var retireResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/revisions/rev-1:retire",
            new ServiceEndpoints.ServiceIdentityHttpRequest("tenant", "app", "ns"));
        var defaultResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders:default-serving",
            new ServiceEndpoints.SetDefaultServingRevisionHttpRequest("tenant", "app", "ns", "rev-1"));
        var activateResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders:activate",
            new ServiceEndpoints.ActivateServiceRevisionHttpRequest("tenant", "app", "ns", "rev-1"));

        prepareResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        retireResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        defaultResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        activateResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        host.CommandPort.PrepareRevisionCommand!.RevisionId.Should().Be("rev-1");
        host.CommandPort.PublishRevisionCommand!.RevisionId.Should().Be("rev-1");
        host.CommandPort.RetireServiceRevisionCommand!.RevisionId.Should().Be("rev-1");
        host.CommandPort.SetDefaultServingRevisionCommand!.RevisionId.Should().Be("rev-1");
        host.CommandPort.ActivateServiceRevisionCommand!.RevisionId.Should().Be("rev-1");
    }

    [Fact]
    public async Task ServingEndpoints_ShouldDispatchCommandsAndReturnSnapshots()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryPort.GetServiceDeploymentsResult = new ServiceDeploymentCatalogSnapshot(
            "tenant:app:ns:orders",
            [
                new ServiceDeploymentSnapshot("dep-1", "rev-1", "actor-1", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            DateTimeOffset.UtcNow);
        host.QueryPort.GetServiceServingSetResult = new ServiceServingSetSnapshot(
            "tenant:app:ns:orders",
            3,
            "rollout-1",
            [
                new ServiceServingTargetSnapshot("dep-1", "rev-1", "actor-1", 100, ServiceServingState.Active.ToString(), ["chat"]),
            ],
            DateTimeOffset.UtcNow);
        host.QueryPort.GetServiceRolloutResult = new ServiceRolloutSnapshot(
            "tenant:app:ns:orders",
            "rollout-1",
            "Rollout",
            ServiceRolloutStatus.InProgress.ToString(),
            0,
            [
                new ServiceRolloutStageSnapshot(
                    "stage-1",
                    0,
                    [
                        new ServiceServingTargetSnapshot("dep-1", "rev-1", "actor-1", 100, ServiceServingState.Active.ToString(), ["chat"]),
                    ]),
            ],
            [],
            string.Empty,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        host.QueryPort.GetServiceTrafficViewResult = new ServiceTrafficViewSnapshot(
            "tenant:app:ns:orders",
            3,
            "rollout-1",
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot("dep-1", "rev-1", "actor-1", 100, ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);

        var deployResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders:deploy",
            new ServiceEndpoints.ActivateServiceRevisionHttpRequest("tenant", "app", "ns", "rev-2"));
        var servingResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders:serving-targets",
            new ServiceEndpoints.ReplaceServiceServingTargetsHttpRequest(
                "tenant",
                "app",
                "ns",
                [
                    new ServiceEndpoints.ServiceServingTargetHttpRequest("rev-2", 100, "active", ["chat"]),
                ]));
        var rolloutResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/rollouts",
            new ServiceEndpoints.StartServiceRolloutHttpRequest(
                "tenant",
                "app",
                "ns",
                "rollout-2",
                "Rollout 2",
                [
                    new ServiceEndpoints.ServiceRolloutStageHttpRequest(
                        "stage-1",
                        [
                            new ServiceEndpoints.ServiceServingTargetHttpRequest("rev-2", 100, "active", ["chat"]),
                        ]),
                ]));

        var deployments = await host.Client.GetFromJsonAsync<ServiceDeploymentCatalogSnapshot>("/api/services/orders/deployments?tenantId=tenant&appId=app&namespace=ns");
        var serving = await host.Client.GetFromJsonAsync<ServiceServingSetSnapshot>("/api/services/orders/serving?tenantId=tenant&appId=app&namespace=ns");
        var rollout = await host.Client.GetFromJsonAsync<ServiceRolloutSnapshot>("/api/services/orders/rollouts?tenantId=tenant&appId=app&namespace=ns");
        var traffic = await host.Client.GetFromJsonAsync<ServiceTrafficViewSnapshot>("/api/services/orders/traffic?tenantId=tenant&appId=app&namespace=ns");

        deployResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        servingResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        rolloutResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.ActivateServiceRevisionCommand!.RevisionId.Should().Be("rev-2");
        host.CommandPort.ReplaceServiceServingTargetsCommand!.Targets.Should().ContainSingle();
        host.CommandPort.StartServiceRolloutCommand!.Plan.RolloutId.Should().Be("rollout-2");
        deployments!.Deployments.Should().ContainSingle();
        serving!.Targets.Should().ContainSingle();
        rollout!.RolloutId.Should().Be("rollout-1");
        traffic!.Endpoints.Should().ContainSingle(x => x.EndpointId == "chat");
        host.QueryPort.LastGetServiceDeploymentsIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "orders",
        });
        host.QueryPort.LastGetServiceServingSetIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "orders",
        });
        host.QueryPort.LastGetServiceRolloutIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "orders",
        });
        host.QueryPort.LastGetServiceTrafficViewIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "orders",
        });
    }

    [Fact]
    public async Task ReplaceServingTargetsAsync_WhenAuthenticatedIdentityConflictsWithBody_ShouldUseClaimIdentity()
    {
        await using var host = await EndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/orders:serving-targets")
        {
            Content = JsonContent.Create(new ServiceEndpoints.ReplaceServiceServingTargetsHttpRequest(
                "spoof-tenant",
                "spoof-app",
                "spoof-ns",
                [
                    new ServiceEndpoints.ServiceServingTargetHttpRequest("rev-1", 100),
                ])),
        };
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-claim");
        request.Headers.Add("X-Test-App-Id", "app-claim");
        request.Headers.Add("X-Test-Namespace", "ns-claim");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.ReplaceServiceServingTargetsCommand!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant-claim",
            AppId = "app-claim",
            Namespace = "ns-claim",
            ServiceId = "orders",
        });
    }

    [Fact]
    public async Task ServingActionEndpoints_ShouldDispatchDeactivateAndRolloutLifecycleCommands()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var deactivateResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/deployments/dep-2:deactivate",
            new ServiceEndpoints.ServiceIdentityHttpRequest("tenant", "app", "ns"));
        var advanceResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/rollouts/rollout-2:advance",
            new ServiceEndpoints.ServiceIdentityHttpRequest("tenant", "app", "ns"));
        var pauseResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/rollouts/rollout-2:pause",
            new ServiceEndpoints.RolloutActionHttpRequest("tenant", "app", "ns", "hold"));
        var resumeResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/rollouts/rollout-2:resume",
            new ServiceEndpoints.ServiceIdentityHttpRequest("tenant", "app", "ns"));
        var rollbackResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/rollouts/rollout-2:rollback",
            new ServiceEndpoints.RolloutActionHttpRequest("tenant", "app", "ns", "rollback-now"));
        var pauseReceipt = await pauseResponse.Content.ReadFromJsonAsync<ServiceCommandAcceptedReceipt>();
        var resumeReceipt = await resumeResponse.Content.ReadFromJsonAsync<ServiceCommandAcceptedReceipt>();
        var rollbackReceipt = await rollbackResponse.Content.ReadFromJsonAsync<ServiceCommandAcceptedReceipt>();

        deactivateResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        advanceResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        pauseResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        resumeResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        rollbackResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        pauseResponse.Headers.Location.Should().NotBeNull();
        pauseResponse.Headers.Location!.ToString().Should().Be("/api/services/orders/rollouts/commands/cmd-pause-rollout");
        resumeResponse.Headers.Location.Should().NotBeNull();
        resumeResponse.Headers.Location!.ToString().Should().Be("/api/services/orders/rollouts/commands/cmd-resume-rollout");
        rollbackResponse.Headers.Location.Should().NotBeNull();
        rollbackResponse.Headers.Location!.ToString().Should().Be("/api/services/orders/rollouts/commands/cmd-rollback-rollout");
        pauseReceipt.Should().BeEquivalentTo(new ServiceCommandAcceptedReceipt(
            "rollout-actor",
            "cmd-pause-rollout",
            "corr-pause-rollout"));
        resumeReceipt.Should().BeEquivalentTo(new ServiceCommandAcceptedReceipt(
            "rollout-actor",
            "cmd-resume-rollout",
            "corr-resume-rollout"));
        rollbackReceipt.Should().BeEquivalentTo(new ServiceCommandAcceptedReceipt(
            "rollout-actor",
            "cmd-rollback-rollout",
            "corr-rollback-rollout"));

        host.CommandPort.DeactivateServiceDeploymentCommand.Should().NotBeNull();
        host.CommandPort.DeactivateServiceDeploymentCommand!.DeploymentId.Should().Be("dep-2");
        host.CommandPort.AdvanceServiceRolloutCommand.Should().NotBeNull();
        host.CommandPort.AdvanceServiceRolloutCommand!.RolloutId.Should().Be("rollout-2");
        host.CommandPort.PauseServiceRolloutCommand.Should().NotBeNull();
        host.CommandPort.PauseServiceRolloutCommand!.RolloutId.Should().Be("rollout-2");
        host.CommandPort.PauseServiceRolloutCommand.Reason.Should().Be("hold");
        host.CommandPort.ResumeServiceRolloutCommand.Should().NotBeNull();
        host.CommandPort.ResumeServiceRolloutCommand!.RolloutId.Should().Be("rollout-2");
        host.CommandPort.RollbackServiceRolloutCommand.Should().NotBeNull();
        host.CommandPort.RollbackServiceRolloutCommand!.RolloutId.Should().Be("rollout-2");
        host.CommandPort.RollbackServiceRolloutCommand.Reason.Should().Be("rollback-now");
    }

    [Fact]
    public async Task RolloutCommandObservationEndpoint_ShouldReturnObservationSnapshot()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryPort.GetServiceRolloutCommandObservationResult = new ServiceRolloutCommandObservationSnapshot(
            "cmd-pause-rollout",
            "corr-pause-rollout",
            "tenant:app:ns:orders",
            "rollout-2",
            ServiceRolloutStatus.Paused,
            true,
            17,
            DateTimeOffset.Parse("2026-04-22T10:00:00+00:00"));

        var observation = await host.Client.GetFromJsonAsync<ServiceRolloutCommandObservationSnapshot>(
            "/api/services/orders/rollouts/commands/cmd-pause-rollout?tenantId=tenant&appId=app&namespace=ns");

        observation.Should().NotBeNull();
        observation!.Status.Should().Be(ServiceRolloutStatus.Paused);
        observation.WasNoOp.Should().BeTrue();
        observation.StateVersion.Should().Be(17);
        host.QueryPort.LastGetServiceRolloutCommandObservation.Should().NotBeNull();
        host.QueryPort.LastGetServiceRolloutCommandObservation!.Value.commandId.Should().Be("cmd-pause-rollout");
        host.QueryPort.LastGetServiceRolloutCommandObservation!.Value.identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "orders",
        });
    }

    [Fact]
    public async Task QueryEndpoints_ShouldReturnCatalogSnapshots()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryPort.ListServicesResult =
        [
            new ServiceCatalogSnapshot(
                "tenant/app/ns/orders",
                "tenant",
                "app",
                "ns",
                "orders",
                "Orders",
                "rev-1",
                "rev-1",
                "dep-1",
                "actor-1",
                "active",
                [
                    new ServiceEndpointSnapshot("submit", "Submit", "command", "req", string.Empty, "desc"),
                ],
                [],
                DateTimeOffset.Parse("2026-03-14T00:00:00+00:00"),
                new ServiceExternalExposureSnapshot(
                    "aevatar-orders",
                    DateTimeOffset.Parse("2026-06-11T01:02:03+00:00"),
                    ServiceRegistrationStatus.Registered,
                    "nyx-svc-1",
                    "hash-1",
                    "hash-1",
                    "",
                    2,
                    null,
                    "kid-1",
                    true,
                    42)),
            new ServiceCatalogSnapshot(
                "tenant/app/ns/billing",
                "tenant",
                "app",
                "ns",
                "billing",
                "Billing",
                "rev-2",
                "rev-2",
                "dep-2",
                "actor-2",
                "active",
                [
                    new ServiceEndpointSnapshot("charge", "Charge", "command", "req", string.Empty, "charge"),
                ],
                [],
                DateTimeOffset.Parse("2026-03-14T00:10:00+00:00")),
            new ServiceCatalogSnapshot(
                "tenant/app/ns/search",
                "tenant",
                "app",
                "ns",
                "search",
                "Search",
                "rev-3",
                "rev-3",
                "dep-3",
                "actor-3",
                "active",
                [
                    new ServiceEndpointSnapshot("query", "Query", "command", "req", string.Empty, "query"),
                ],
                [],
                DateTimeOffset.Parse("2026-03-14T00:20:00+00:00")),
        ];
        host.QueryPort.GetServiceResult = host.QueryPort.ListServicesResult[0];
        host.InvocationCatalogReader.CatalogsByServiceKey["tenant:app:ns:orders"] = BuildInvocationCatalog(
            "tenant:app:ns:orders",
            "submit",
            ServiceInvokeReadinessStatus.Ready,
            ServiceInvokeUnavailableReason.Unspecified,
            "rev-1",
            "dep-1",
            "actor-1",
            21);
        host.InvocationCatalogReader.CatalogsByServiceKey["tenant:app:ns:billing"] = BuildInvocationCatalog(
            "tenant:app:ns:billing",
            "charge",
            ServiceInvokeReadinessStatus.Unavailable,
            ServiceInvokeUnavailableReason.PreparedArtifactMissing,
            "rev-2",
            "dep-2",
            "actor-2",
            22);
        host.QueryPort.GetServiceRevisionsResult = new ServiceRevisionCatalogSnapshot(
            "tenant/app/ns/orders",
            [
                new ServiceRevisionSnapshot(
                    "rev-1",
                    "static",
                    "published",
                    "artifact",
                    string.Empty,
                    [
                        new ServiceEndpointSnapshot("submit", "Submit", "command", "req", string.Empty, "desc"),
                    ],
                    DateTimeOffset.Parse("2026-03-14T00:00:00+00:00"),
                    DateTimeOffset.Parse("2026-03-14T00:01:00+00:00"),
                    DateTimeOffset.Parse("2026-03-14T00:02:00+00:00"),
                    null),
            ],
            DateTimeOffset.Parse("2026-03-14T00:03:00+00:00"));

        var listResponse = await host.Client.GetFromJsonAsync<List<ServiceEndpoints.ServiceWithInvokeReadinessHttpResponse>>("/api/services/?take=10");
        var getResponse = await host.Client.GetFromJsonAsync<ServiceEndpoints.ServiceWithInvokeReadinessHttpResponse>("/api/services/orders?tenantId=tenant&appId=app&namespace=ns");
        var revisionResponse = await host.Client.GetFromJsonAsync<ServiceRevisionCatalogSnapshot>("/api/services/orders/revisions?tenantId=tenant&appId=app&namespace=ns");

        listResponse.Should().HaveCount(3);
        listResponse!.Single(x => x.ServiceId == "orders").InvokeReady.Should().BeTrue();
        listResponse.Single(x => x.ServiceId == "orders").InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Ready.ToString());
        listResponse.Single(x => x.ServiceId == "orders").InvokeUnavailableReason.Should().BeNull();
        listResponse.Single(x => x.ServiceId == "billing").InvokeReady.Should().BeFalse();
        listResponse.Single(x => x.ServiceId == "billing").InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unavailable.ToString());
        listResponse.Single(x => x.ServiceId == "billing").InvokeUnavailableReason.Should().Be(ServiceInvokeUnavailableReason.PreparedArtifactMissing.ToString());
        listResponse.Single(x => x.ServiceId == "search").InvokeReady.Should().BeFalse();
        listResponse.Single(x => x.ServiceId == "search").InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unspecified.ToString());
        listResponse.Single(x => x.ServiceId == "search").InvokeUnavailableReason.Should().BeNull();
        getResponse!.ServiceId.Should().Be("orders");
        getResponse.InvokeReady.Should().BeTrue();
        getResponse.InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Ready.ToString());
        getResponse.InvokeUnavailableReason.Should().BeNull();
        getResponse.ExternalExposure.Should().NotBeNull();
        getResponse.ExternalExposure!.NyxidSlug.Should().Be("aevatar-orders");
        getResponse.ExternalExposure.RegisteredAt.Should().Be(DateTimeOffset.Parse("2026-06-11T01:02:03+00:00"));
        getResponse.ExternalExposure.Status.Should().Be(ServiceRegistrationStatus.Registered.ToString());
        getResponse.ExternalExposure.NyxidServiceId.Should().Be("nyx-svc-1");
        getResponse.ExternalExposure.DesiredSpecHash.Should().Be("hash-1");
        getResponse.ExternalExposure.RegisteredSpecHash.Should().Be("hash-1");
        getResponse.ExternalExposure.Attempt.Should().Be(2);
        getResponse.ExternalExposure.CredentialKid.Should().Be("kid-1");
        getResponse.ExternalExposure.ExposureDesired.Should().BeTrue();
        getResponse.ExternalExposure.SourceStateVersion.Should().Be(42);
        revisionResponse!.Revisions.Should().ContainSingle();
        host.QueryPort.LastListServicesTake.Should().Be(10);
        host.QueryPort.LastGetServiceIdentity!.ServiceId.Should().Be("orders");
        host.QueryPort.LastGetServiceRevisionsIdentity!.Namespace.Should().Be("ns");
    }

    [Fact]
    public async Task GetServiceAsync_WhenAuthenticatedIdentityConflictsWithQuery_ShouldUseClaimIdentity()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryPort.GetServiceResult = new ServiceCatalogSnapshot(
            "tenant/app/ns/orders",
            "tenant-claim",
            "app-claim",
            "ns-claim",
            "orders",
            "Orders",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            DateTimeOffset.UtcNow);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/services/orders?tenantId=spoof-tenant&appId=spoof-app&namespace=spoof-ns");
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-claim");
        request.Headers.Add("X-Test-App-Id", "app-claim");
        request.Headers.Add("X-Test-Namespace", "ns-claim");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.QueryPort.LastGetServiceIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant-claim",
            AppId = "app-claim",
            Namespace = "ns-claim",
            ServiceId = "orders",
        });
    }

    [Fact]
    public async Task GetServiceAsync_ShouldReturnUnspecifiedInvokeReadiness_WhenInvocationCatalogHasNoEntries()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryPort.GetServiceResult = new ServiceCatalogSnapshot(
            "tenant/app/ns/orders",
            "tenant",
            "app",
            "ns",
            "orders",
            "Orders",
            "rev-1",
            "rev-1",
            "dep-1",
            "actor-1",
            "active",
            [
                new ServiceEndpointSnapshot("submit", "Submit", "command", "req", string.Empty, "desc"),
            ],
            [],
            DateTimeOffset.Parse("2026-03-14T00:00:00+00:00"));
        host.InvocationCatalogReader.CatalogsByServiceKey["tenant:app:ns:orders"] = new ServiceInvocationCatalogSnapshot(
            "tenant:app:ns:orders",
            [],
            DateTimeOffset.Parse("2026-03-14T00:05:00+00:00"),
            5,
            "evt-5",
            2,
            3,
            4);

        var response = await host.Client.GetFromJsonAsync<ServiceEndpoints.ServiceWithInvokeReadinessHttpResponse>(
            "/api/services/orders?tenantId=tenant&appId=app&namespace=ns");

        response.Should().NotBeNull();
        response!.ServiceId.Should().Be("orders");
        response.InvokeReady.Should().BeFalse();
        response.InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unspecified.ToString());
        response.InvokeUnavailableReason.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_ShouldPackBase64PayloadIntoAny()
    {
        await using var host = await EndpointTestHost.StartAsync();
        var payload = Convert.ToBase64String([1, 2, 3, 4]);

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            null,
            null,
            "type.googleapis.com/demo.Request",
            payload));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Be("/api/scopes/tenant/services/orders/runs/run-1");
        var receipt = await response.Content.ReadFromJsonAsync<ServiceInvocationAcceptedReceipt>();
        receipt.Should().NotBeNull();
        receipt!.RunId.Should().Be("run-1");
        receipt.StatusUrl.Should().Be("/api/scopes/tenant/services/orders/runs/run-1");
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.EndpointId.Should().Be("chat");
        host.InvocationPort.LastRequest.Payload.TypeUrl.Should().Be("type.googleapis.com/demo.Request");
        host.InvocationPort.LastRequest.Payload.Value.ToByteArray().Should().Equal([1, 2, 3, 4]);
        host.InvocationPort.LastRequest.CommandId.Should().BeEmpty();
        host.InvocationPort.LastRequest.CorrelationId.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WithoutPayloadBase64_ShouldPackEmptyPayload()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            "cmd-2",
            "corr-2",
            "type.googleapis.com/demo.Empty",
            null));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Payload.Value.Length.Should().Be(0);
        host.InvocationPort.LastRequest.CommandId.Should().Be("cmd-2");
        host.InvocationPort.LastRequest.CorrelationId.Should().Be("corr-2");
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturnInvokeUnavailableBody_WhenReadinessRejectsInvocation()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.InvocationPort.ExceptionFactory = _ => new ServiceInvokeReadinessException(
            "Endpoint 'chat' is not invoke-ready.",
            new ServiceInvokeReadinessSnapshot(
                "tenant:app:ns:orders",
                "chat",
                ServiceInvokeReadinessStatus.Unavailable,
                ServiceInvokeUnavailableReason.PreparedArtifactMissing,
                "rev-1",
                "dep-1",
                "actor-1",
                DateTimeOffset.Parse("2026-06-05T02:00:00+00:00"),
                8,
                "evt-8",
                3,
                5,
                7));

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            null,
            null,
            "type.googleapis.com/google.protobuf.Empty",
            null));
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].GetString().Should().Be("SERVICE_INVOKE_UNAVAILABLE");
        body["message"].GetString().Should().Be("Endpoint 'chat' is not invoke-ready.");
        body["readinessStatus"].GetString().Should().Be(ServiceInvokeReadinessStatus.Unavailable.ToString());
        body["reason"].GetString().Should().Be(ServiceInvokeUnavailableReason.PreparedArtifactMissing.ToString());
        body["serviceKey"].GetString().Should().Be("tenant:app:ns:orders");
        body["revisionId"].GetString().Should().Be("rev-1");
        body["endpointId"].GetString().Should().Be("chat");
        body["deploymentId"].GetString().Should().Be("dep-1");
        body["observedAt"].GetDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-06-05T02:00:00+00:00"));
        body["aggregateStateVersion"].GetInt64().Should().Be(8);
        body["sourceCatalogVersion"].GetInt64().Should().Be(3);
        body["sourceServingVersion"].GetInt64().Should().Be(5);
        body["sourceRevisionVersion"].GetInt64().Should().Be(7);
    }

    [Fact]
    public async Task InvokeAsync_WithPayloadJson_ShouldPackTypedAnyAndForwardRevisionId()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.CatalogReader.Service = new ServiceCatalogSnapshot(
            ServiceKey: "tenant:app:ns:orders",
            TenantId: "tenant",
            AppId: "app",
            Namespace: "ns",
            ServiceId: "orders",
            DisplayName: "Orders",
            DefaultServingRevisionId: "rev-active",
            ActiveServingRevisionId: "rev-active",
            DeploymentId: "dep-1",
            PrimaryActorId: "actor-1",
            DeploymentStatus: "Active",
            Endpoints: [],
            PolicyIds: [],
            UpdatedAt: DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            "tenant:app:ns:orders",
            "rev-active",
            new PreparedServiceRevisionArtifact
            {
                ProtocolDescriptorSet = BuildProtocolDescriptorSetFor(ServiceIdentity.Descriptor),
            });

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            null,
            null,
            "type.googleapis.com/aevatar.gagentservice.ServiceIdentity",
            null,
            PayloadJson: """{"tenantId":"hello-tenant","serviceId":"orders"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Payload.TypeUrl.Should().Be("type.googleapis.com/aevatar.gagentservice.ServiceIdentity");
        host.InvocationPort.LastRequest.RevisionId.Should().Be("rev-active");
        var decoded = ServiceIdentity.Parser.ParseFrom(host.InvocationPort.LastRequest.Payload.Value);
        decoded.TenantId.Should().Be("hello-tenant");
        decoded.ServiceId.Should().Be("orders");
    }

    [Fact]
    public async Task OpenApiAsync_ShouldReturnAnonymousReadOnlyToolDocument()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.CatalogReader.Service = new ServiceCatalogSnapshot(
            ServiceKey: "tenant:app:ns:orders",
            TenantId: "tenant",
            AppId: "app",
            Namespace: "ns",
            ServiceId: "orders",
            DisplayName: "Orders",
            DefaultServingRevisionId: "rev-active",
            ActiveServingRevisionId: "rev-active",
            DeploymentId: "dep-1",
            PrimaryActorId: "actor-1",
            DeploymentStatus: "Active",
            Endpoints:
            [
                new ServiceEndpointSnapshot(
                    "submit",
                    "Submit",
                    "Command",
                    "type.googleapis.com/aevatar.gagentservice.ServiceIdentity",
                    string.Empty,
                    "submit command"),
            ],
            PolicyIds: [],
            UpdatedAt: DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            "tenant:app:ns:orders",
            "rev-active",
            new PreparedServiceRevisionArtifact
            {
                RevisionId = "rev-active",
                ImplementationKind = ServiceImplementationKind.Static,
                ProtocolDescriptorSet = BuildProtocolDescriptorSetFor(ServiceIdentity.Descriptor),
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/services/orders/openapi.json?tenantId=tenant&appId=app&namespace=ns");
        request.Headers.Add("X-Test-Authenticated", "false");
        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("openapi").GetString().Should().Be("3.1.0");
        var operation = root.GetProperty("paths")
            .GetProperty("/api/services/orders/invoke/submit")
            .GetProperty("post");
        operation.GetProperty("operationId").GetString().Should().Be("orders_submit");
        operation.GetProperty("x-aevatar-tool").GetProperty("serviceKey").GetString().Should().Be("tenant:app:ns:orders");
        operation.GetProperty("x-aevatar-tool").GetProperty("endpointId").GetString().Should().Be("submit");
        operation.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("properties")
            .TryGetProperty("tenantId", out _)
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_WithPayloadJsonAndExplicitRevision_ShouldUseRequestedRevision()
    {
        await using var host = await EndpointTestHost.StartAsync();
        await host.RevisionCatalog.UpsertRevisionAsync(
            "tenant:app:ns:orders",
            "rev-explicit",
            new PreparedServiceRevisionArtifact
            {
                ProtocolDescriptorSet = BuildProtocolDescriptorSetFor(ServiceIdentity.Descriptor),
            });

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            null,
            null,
            "type.googleapis.com/aevatar.gagentservice.ServiceIdentity",
            null,
            PayloadJson: """{"tenantId":"named-tenant"}""",
            RevisionId: "rev-explicit"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest!.RevisionId.Should().Be("rev-explicit");
    }

    [Fact]
    public async Task InvokeAsync_WithBothPayloadJsonAndBase64_ShouldReturnBadRequest()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            null,
            null,
            "type.googleapis.com/aevatar.gagentservice.ServiceIdentity",
            "AAAA",
            PayloadJson: """{"tenantId":"hi"}"""));
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["code"].Should().Be("INVALID_SERVICE_INVOKE_REQUEST");
        body["message"].Should().Contain("mutually exclusive");
    }

    [Fact]
    public async Task InvokeAsync_WithPayloadJson_ShouldReturnBadRequest_WhenTypeUrlNotInRevision()
    {
        await using var host = await EndpointTestHost.StartAsync();
        await host.RevisionCatalog.UpsertRevisionAsync(
            "tenant:app:ns:orders",
            "rev-active",
            new PreparedServiceRevisionArtifact
            {
                ProtocolDescriptorSet = BuildProtocolDescriptorSetFor(ServiceIdentity.Descriptor),
            });

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            null,
            null,
            "type.googleapis.com/demo.Unknown",
            null,
            PayloadJson: """{"foo":"bar"}""",
            RevisionId: "rev-active"));
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["code"].Should().Be("INVALID_SERVICE_INVOKE_REQUEST");
        body["message"].Should().Contain("not found in revision");
    }

    [Fact]
    public async Task InvokeAsync_WithPayloadJson_ShouldReturnBadRequest_WhenJsonIsMalformed()
    {
        await using var host = await EndpointTestHost.StartAsync();
        await host.RevisionCatalog.UpsertRevisionAsync(
            "tenant:app:ns:orders",
            "rev-active",
            new PreparedServiceRevisionArtifact
            {
                ProtocolDescriptorSet = BuildProtocolDescriptorSetFor(ServiceIdentity.Descriptor),
            });

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            null,
            null,
            "type.googleapis.com/aevatar.gagentservice.ServiceIdentity",
            null,
            PayloadJson: "{this is not json",
            RevisionId: "rev-active"));
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["code"].Should().Be("INVALID_SERVICE_INVOKE_REQUEST");
        body["message"].Should().Contain("payloadJson");
    }

    [Fact]
    public async Task InvokeAsync_WithPayloadJson_ShouldReturnBadRequest_WhenNoActiveRevision()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            null,
            null,
            "type.googleapis.com/aevatar.gagentservice.ServiceIdentity",
            null,
            PayloadJson: """{"tenantId":"hi"}"""));
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["code"].Should().Be("INVALID_SERVICE_INVOKE_REQUEST");
        body["message"].Should().Contain("revisionId");
    }

    [Fact]
    public async Task CreateRevisionAsync_WithUnsupportedImplementationKind_ShouldFail()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-1",
            "unknown",
            null,
            null,
            null));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task CreateRevisionAsync_WhenAuthenticatedIdentityMissingClaims_ShouldReturnForbiddenBeforeParsingKind()
    {
        await using var host = await EndpointTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/orders/revisions")
        {
            Content = JsonContent.Create(new ServiceEndpoints.CreateRevisionHttpRequest(
                "tenant",
                "app",
                "ns",
                "rev-1",
                "unknown",
                null,
                null,
                null)),
        };
        request.Headers.Add("X-Test-Authenticated", "true");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.CommandPort.CreateRevisionCommand.Should().BeNull();
    }

    [Fact]
    public async Task CreateRevisionAsync_ShouldAllowMissingImplementationPayloads_AndFallbackToEmptySpecs()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var staticResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-static-empty",
            "static",
            null,
            null,
            null));
        staticResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.StaticSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.ActorTypeName.Should().BeEmpty();
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.Endpoints.Should().BeEmpty();

        var scriptingResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-script-empty",
            "scripting",
            null,
            null,
            null));
        scriptingResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.ScriptingSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.ScriptingSpec.ScriptId.Should().BeEmpty();
        host.CommandPort.CreateRevisionCommand.Spec.ScriptingSpec.DefinitionActorId.Should().BeEmpty();

        var workflowResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-workflow-empty",
            "workflow",
            null,
            null,
            null));
        workflowResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.WorkflowSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.WorkflowName.Should().BeEmpty();
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.DefinitionActorId.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateServiceAsync_ShouldMapChatEndpointKind()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/", new ServiceEndpoints.CreateServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            "service-chat",
            "Chat Service",
            [
                new ServiceEndpoints.ServiceEndpointHttpRequest(
                    "chat",
                    "Chat",
                    "chat",
                    "type.googleapis.com/demo.ChatRequest",
                    "type.googleapis.com/demo.ChatResponse",
                    "chat endpoint"),
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateServiceCommand!.Spec.Endpoints.Should().ContainSingle();
        host.CommandPort.CreateServiceCommand.Spec.Endpoints[0].Kind.Should().Be(ServiceEndpointKind.Chat);
    }

    [Fact]
    public async Task QueryEndpoints_ShouldUseEmptyIdentityPartsAndDefaultTake_WhenOmitted()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryPort.GetServiceResult = new ServiceCatalogSnapshot(
            "tenant/app/ns/orders",
            string.Empty,
            string.Empty,
            string.Empty,
            "orders",
            "Orders",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            DateTimeOffset.UtcNow);
        host.QueryPort.GetServiceRevisionsResult = new ServiceRevisionCatalogSnapshot(
            "tenant/app/ns/orders",
            [],
            DateTimeOffset.UtcNow);

        var listResponse = await host.Client.GetFromJsonAsync<List<ServiceEndpoints.ServiceWithInvokeReadinessHttpResponse>>("/api/services");
        var getResponse = await host.Client.GetFromJsonAsync<ServiceEndpoints.ServiceWithInvokeReadinessHttpResponse>("/api/services/orders");
        var revisionsResponse = await host.Client.GetFromJsonAsync<ServiceRevisionCatalogSnapshot>("/api/services/orders/revisions");

        listResponse.Should().BeEmpty();
        getResponse.Should().NotBeNull();
        getResponse!.InvokeReady.Should().BeFalse();
        getResponse.InvokeReadinessStatus.Should().Be(ServiceInvokeReadinessStatus.Unspecified.ToString());
        getResponse.InvokeUnavailableReason.Should().BeNull();
        revisionsResponse.Should().NotBeNull();
        host.QueryPort.LastListServicesTake.Should().Be(200);
        host.QueryPort.LastGetServiceIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = string.Empty,
            AppId = string.Empty,
            Namespace = string.Empty,
            ServiceId = "orders",
        });
        host.QueryPort.LastGetServiceRevisionsIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = string.Empty,
            AppId = string.Empty,
            Namespace = string.Empty,
            ServiceId = "orders",
        });
    }

    [Fact]
    public async Task InvokeAsync_WithoutPayloadTypeUrl_ShouldFail()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            "cmd-1",
            "corr-1",
            null,
            null));
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body!["code"].Should().Be("INVALID_SERVICE_INVOKE_REQUEST");
        body["message"].Should().Contain("payloadTypeUrl");
    }

    [Fact]
    public async Task GetServiceAsync_ShouldReturnNullBody_WhenServiceDoesNotExist()
    {
        await using var host = await EndpointTestHost.StartAsync();
        // GetServiceResult defaults to null

        var response = await host.Client.GetAsync("/api/services/nonexistent?tenantId=t&appId=a&namespace=n");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("null");
    }

    [Fact]
    public async Task GetServiceRevisionsAsync_ShouldReturnNullBody_WhenNoRevisions()
    {
        await using var host = await EndpointTestHost.StartAsync();
        // GetServiceRevisionsResult defaults to null

        var response = await host.Client.GetAsync("/api/services/orders/revisions?tenantId=t&appId=a&namespace=n");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("null");
    }

    [Fact]
    public async Task GetServiceDeploymentsAsync_ShouldReturnNullBody_WhenNoDeployments()
    {
        await using var host = await EndpointTestHost.StartAsync();
        // GetServiceDeploymentsResult defaults to null

        var response = await host.Client.GetAsync("/api/services/orders/deployments?tenantId=t&appId=a&namespace=n");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("null");
    }

    [Fact]
    public async Task ListServicesAsync_ShouldReturnEmptyArray_WhenNoMatches()
    {
        await using var host = await EndpointTestHost.StartAsync();
        // ListServicesResult defaults to []

        var result = await host.Client.GetFromJsonAsync<List<ServiceCatalogSnapshot>>("/api/services/?tenantId=t&appId=a&namespace=n");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetServingSetAsync_ShouldReturnNullBody_WhenNoServingSet()
    {
        await using var host = await EndpointTestHost.StartAsync();
        // GetServiceServingSetResult defaults to null

        var response = await host.Client.GetAsync("/api/services/orders/serving?tenantId=t&appId=a&namespace=n");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("null");
    }

    [Fact]
    public async Task GetRolloutAsync_ShouldReturnNullBody_WhenNoRollout()
    {
        await using var host = await EndpointTestHost.StartAsync();
        // GetServiceRolloutResult defaults to null

        var response = await host.Client.GetAsync("/api/services/orders/rollouts?tenantId=t&appId=a&namespace=n");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("null");
    }

    [Fact]
    public async Task GetTrafficViewAsync_ShouldReturnNullBody_WhenNoTraffic()
    {
        await using var host = await EndpointTestHost.StartAsync();
        // GetServiceTrafficViewResult defaults to null

        var response = await host.Client.GetAsync("/api/services/orders/traffic?tenantId=t&appId=a&namespace=n");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("null");
    }

    [Fact]
    public async Task InvokeAsync_ShouldMapCallerIdentity_WhenProvided()
    {
        await using var host = await EndpointTestHost.StartAsync();
        var payload = Convert.ToBase64String([5, 6, 7]);

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/run", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            "cmd-5",
            "corr-5",
            "type.googleapis.com/demo.Run",
            payload,
            "caller-svc-key",
            "caller-tenant",
            "caller-app"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.EndpointId.Should().Be("run");
        host.InvocationPort.LastRequest.Caller.ServiceKey.Should().Be("caller-svc-key");
        host.InvocationPort.LastRequest.Caller.TenantId.Should().Be("caller-tenant");
        host.InvocationPort.LastRequest.Caller.AppId.Should().Be("caller-app");
    }

    [Fact]
    public async Task InvokeAsync_WhenAuthenticatedIdentityConflictsWithBody_ShouldIgnoreSpoofedCallerIdentity()
    {
        await using var host = await EndpointTestHost.StartAsync();
        var payload = Convert.ToBase64String([8, 9, 10]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/orders/invoke/run")
        {
            Content = JsonContent.Create(new ServiceEndpoints.InvokeServiceHttpRequest(
                "spoof-tenant",
                "spoof-app",
                "spoof-ns",
                "cmd-6",
                "corr-6",
                "type.googleapis.com/demo.Run",
                payload,
                "tenant-claim/app-claim/ns-claim/allowed-caller",
                "spoof-caller-tenant",
                "spoof-caller-app")),
        };
        request.Headers.Add("X-Test-Authenticated", "true");
        request.Headers.Add("X-Test-Tenant-Id", "tenant-claim");
        request.Headers.Add("X-Test-App-Id", "app-claim");
        request.Headers.Add("X-Test-Namespace", "ns-claim");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant-claim",
            AppId = "app-claim",
            Namespace = "ns-claim",
            ServiceId = "orders",
        });
        host.InvocationPort.LastRequest.Caller.ServiceKey.Should().BeEmpty();
        host.InvocationPort.LastRequest.Caller.TenantId.Should().Be("tenant-claim");
        host.InvocationPort.LastRequest.Caller.AppId.Should().Be("app-claim");
    }

    [Fact]
    public async Task CreateServiceAsync_ShouldMapMultipleEndpoints()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/", new ServiceEndpoints.CreateServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            "multi-ep",
            "Multi Endpoint Service",
            [
                new ServiceEndpoints.ServiceEndpointHttpRequest("submit", "Submit", "command", "type.googleapis.com/demo.Submit", "", ""),
                new ServiceEndpoints.ServiceEndpointHttpRequest("chat", "Chat", "chat", "type.googleapis.com/demo.Chat", "type.googleapis.com/demo.ChatResp", ""),
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateServiceCommand!.Spec.Endpoints.Should().HaveCount(2);
        host.CommandPort.CreateServiceCommand.Spec.Endpoints[0].EndpointId.Should().Be("submit");
        host.CommandPort.CreateServiceCommand.Spec.Endpoints[1].EndpointId.Should().Be("chat");
        host.CommandPort.CreateServiceCommand.Spec.Endpoints[1].Kind.Should().Be(ServiceEndpointKind.Chat);
    }

    [Fact]
    public async Task ReplaceServingTargetsAsync_ShouldMapMultipleTargets()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync(
            "/api/services/orders:serving-targets",
            new ServiceEndpoints.ReplaceServiceServingTargetsHttpRequest(
                "tenant",
                "app",
                "ns",
                [
                    new ServiceEndpoints.ServiceServingTargetHttpRequest("rev-1", 70, "active", ["chat", "submit"]),
                    new ServiceEndpoints.ServiceServingTargetHttpRequest("rev-2", 30, "paused", []),
                ],
                "rollout-x",
                "canary release"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.ReplaceServiceServingTargetsCommand!.Targets.Should().HaveCount(2);
        host.CommandPort.ReplaceServiceServingTargetsCommand.Targets[0].RevisionId.Should().Be("rev-1");
        host.CommandPort.ReplaceServiceServingTargetsCommand.Targets[0].AllocationWeight.Should().Be(70);
        host.CommandPort.ReplaceServiceServingTargetsCommand.Targets[1].ServingState.Should().Be(ServiceServingState.Paused);
        host.CommandPort.ReplaceServiceServingTargetsCommand.RolloutId.Should().Be("rollout-x");
        host.CommandPort.ReplaceServiceServingTargetsCommand.Reason.Should().Be("canary release");
    }

    private sealed class EndpointTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private EndpointTestHost(
            WebApplication app,
            HttpClient client,
            RecordingServiceCommandPort commandPort,
            RecordingServiceQueryPort queryPort,
            RecordingServiceInvocationPort invocationPort,
            FakeServiceCatalogQueryReader catalogReader,
            FakeServiceInvocationCatalogQueryReader invocationCatalogReader,
            FakeServiceRevisionCatalogQueryReader revisionCatalog,
            RecordingWorkflowCapabilityAdmissionService capabilityAdmission)
        {
            _app = app;
            Client = client;
            CommandPort = commandPort;
            QueryPort = queryPort;
            InvocationPort = invocationPort;
            CatalogReader = catalogReader;
            InvocationCatalogReader = invocationCatalogReader;
            RevisionCatalog = revisionCatalog;
            CapabilityAdmission = capabilityAdmission;
        }

        public HttpClient Client { get; }

        public RecordingServiceCommandPort CommandPort { get; }

        public RecordingServiceQueryPort QueryPort { get; }

        public RecordingServiceInvocationPort InvocationPort { get; }

        public FakeServiceCatalogQueryReader CatalogReader { get; }

        public FakeServiceInvocationCatalogQueryReader InvocationCatalogReader { get; }

        public FakeServiceRevisionCatalogQueryReader RevisionCatalog { get; }

        public RecordingWorkflowCapabilityAdmissionService CapabilityAdmission { get; }

        public static async Task<EndpointTestHost> StartAsync(Exception? admissionFailure = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var commandPort = new RecordingServiceCommandPort();
            var queryPort = new RecordingServiceQueryPort();
            var invocationPort = new RecordingServiceInvocationPort();
            var catalogReader = new FakeServiceCatalogQueryReader();
            var invocationCatalogReader = new FakeServiceInvocationCatalogQueryReader(catalogReader);
            var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
            var capabilityAdmission = new RecordingWorkflowCapabilityAdmissionService(admissionFailure);
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<IServiceCommandPort>(commandPort);
            builder.Services.AddSingleton<IServiceLifecycleQueryPort>(queryPort);
            builder.Services.AddSingleton<IServiceServingQueryPort>(queryPort);
            builder.Services.AddSingleton<IServiceInvocationPort>(invocationPort);
            builder.Services.AddSingleton<IServiceCatalogQueryReader>(catalogReader);
            builder.Services.AddSingleton<IServiceInvocationCatalogQueryReader>(invocationCatalogReader);
            builder.Services.AddSingleton<IServiceRevisionCatalogQueryReader>(revisionCatalog);
            builder.Services.AddSingleton<IWorkflowExternalCapabilityAdmissionService>(capabilityAdmission);
            builder.Services.AddSingleton<ServiceInvokeReadinessErrorMapper>();
            builder.Services.AddSingleton<IServiceIdentityContextResolver, DefaultServiceIdentityContextResolver>();

            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                if (http.Request.Headers.TryGetValue("X-Test-Authenticated", out var authenticatedValues) &&
                    bool.TryParse(authenticatedValues, out var authenticated) &&
                    authenticated)
                {
                    var claims = new List<Claim>();
                    AddClaims(http, "X-Test-Tenant-Id", AevatarStandardClaimTypes.TenantId, claims);
                    AddClaims(http, "X-Test-App-Id", AevatarStandardClaimTypes.AppId, claims);
                    AddClaims(http, "X-Test-Namespace", AevatarStandardClaimTypes.Namespace, claims);
                    AddClaims(http, "X-Test-Caller-Id", "sub", claims);
                    http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
                }

                await next();
            });
            app.MapGAgentServiceEndpoints();
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var address = addressFeature.Addresses.Single();
            var client = new HttpClient
            {
                BaseAddress = new Uri(address),
            };

            return new EndpointTestHost(
                app,
                client,
                commandPort,
                queryPort,
                invocationPort,
                catalogReader,
                invocationCatalogReader,
                revisionCatalog,
                capabilityAdmission);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }

        private static void AddClaims(HttpContext http, string headerName, string claimType, ICollection<Claim> claims)
        {
            if (!http.Request.Headers.TryGetValue(headerName, out var values))
                return;

            foreach (var value in values.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(claimType, value));
            }
        }
    }

    private sealed class RecordingWorkflowCapabilityAdmissionService :
        IWorkflowExternalCapabilityAdmissionService
    {
        private readonly Exception? _failure;

        public RecordingWorkflowCapabilityAdmissionService(Exception? failure = null)
        {
            _failure = failure;
        }

        public List<WorkflowExternalCapabilityAdmissionRequest> Requests { get; } = [];

        public List<PersistedWorkflowCapabilityAdmissionRequest> PersistedRequests { get; } = [];

        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (_failure is not null)
                return Task.FromException<WorkflowCapabilityAdmissionPlan>(_failure);

            return Task.FromResult(WorkflowCapabilityAdmissionPlanIntegrity.Create(
                request.WorkflowYaml,
                request.InlineWorkflowYamls,
                request.ExecutionMode,
                [],
                []));
        }

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            PersistedRequests.Add(request);
            if (_failure is not null)
                return Task.FromException<WorkflowCapabilityAdmissionPlan>(_failure);

            return Task.FromResult(request.Plan.Clone());
        }
    }

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        public CreateServiceDefinitionCommand? CreateServiceCommand { get; private set; }

        public UpdateServiceDefinitionCommand? UpdateServiceCommand { get; private set; }

        public ReconcileExternalExposureCommand? ReconcileExternalExposureCommand { get; private set; }

        public RetireExternalExposureCommand? RetireExternalExposureCommand { get; private set; }

        public CreateServiceRevisionCommand? CreateRevisionCommand { get; private set; }

        public PrepareServiceRevisionCommand? PrepareRevisionCommand { get; private set; }

        public PublishServiceRevisionCommand? PublishRevisionCommand { get; private set; }

        public RetireServiceRevisionCommand? RetireServiceRevisionCommand { get; private set; }

        public SetDefaultServingRevisionCommand? SetDefaultServingRevisionCommand { get; private set; }

        public ActivateServiceRevisionCommand? ActivateServiceRevisionCommand { get; private set; }

        public DeactivateServiceDeploymentCommand? DeactivateServiceDeploymentCommand { get; private set; }

        public ReplaceServiceServingTargetsCommand? ReplaceServiceServingTargetsCommand { get; private set; }

        public StartServiceRolloutCommand? StartServiceRolloutCommand { get; private set; }

        public AdvanceServiceRolloutCommand? AdvanceServiceRolloutCommand { get; private set; }

        public PauseServiceRolloutCommand? PauseServiceRolloutCommand { get; private set; }

        public ResumeServiceRolloutCommand? ResumeServiceRolloutCommand { get; private set; }

        public RollbackServiceRolloutCommand? RollbackServiceRolloutCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            CreateServiceCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("definition-actor", "cmd-create-service", "corr-create-service"));
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            UpdateServiceCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("definition-actor", "cmd-update-service", "corr-update-service"));
        }

        public Task<ServiceCommandAcceptedReceipt> ReconcileExternalExposureAsync(ReconcileExternalExposureCommand command, CancellationToken ct = default)
        {
            ReconcileExternalExposureCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("definition-actor", "cmd-reconcile-external-exposure", "corr-reconcile-external-exposure"));
        }

        public Task<ServiceCommandAcceptedReceipt> RetireExternalExposureAsync(RetireExternalExposureCommand command, CancellationToken ct = default)
        {
            RetireExternalExposureCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("definition-actor", "cmd-retire-external-exposure", "corr-retire-external-exposure"));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default)
        {
            CreateRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("revision-actor", "cmd-create-revision", "corr-create-revision"));
        }

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default)
        {
            PrepareRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("revision-actor", "cmd-prepare-revision", "corr-prepare-revision"));
        }

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default)
        {
            PublishRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("revision-actor", "cmd-publish-revision", "corr-publish-revision"));
        }

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default)
        {
            RetireServiceRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("revision-actor", "cmd-retire-revision", "corr-retire-revision"));
        }

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            SetDefaultServingRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("definition-actor", "cmd-default-serving", "corr-default-serving"));
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            ActivateServiceRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("deployment-actor", "cmd-activate-serving", "corr-activate-serving"));
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default)
        {
            DeactivateServiceDeploymentCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("deployment-actor", "cmd-deactivate-serving", "corr-deactivate-serving"));
        }

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default)
        {
            ReplaceServiceServingTargetsCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("serving-actor", "cmd-replace-serving", "corr-replace-serving"));
        }

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default)
        {
            StartServiceRolloutCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("rollout-actor", "cmd-start-rollout", "corr-start-rollout"));
        }

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default)
        {
            AdvanceServiceRolloutCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("rollout-actor", "cmd-advance-rollout", "corr-advance-rollout"));
        }

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default)
        {
            PauseServiceRolloutCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("rollout-actor", "cmd-pause-rollout", "corr-pause-rollout"));
        }

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default)
        {
            ResumeServiceRolloutCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("rollout-actor", "cmd-resume-rollout", "corr-resume-rollout"));
        }

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default)
        {
            RollbackServiceRolloutCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("rollout-actor", "cmd-rollback-rollout", "corr-rollback-rollout"));
        }
    }

    private sealed class RecordingServiceQueryPort : IServiceLifecycleQueryPort, IServiceServingQueryPort
    {
        public IReadOnlyList<ServiceCatalogSnapshot> ListServicesResult { get; set; } = [];

        public ServiceCatalogSnapshot? GetServiceResult { get; set; }

        public ServiceRevisionCatalogSnapshot? GetServiceRevisionsResult { get; set; }

        public ServiceDeploymentCatalogSnapshot? GetServiceDeploymentsResult { get; set; }

        public ServiceServingSetSnapshot? GetServiceServingSetResult { get; set; }

        public ServiceRolloutSnapshot? GetServiceRolloutResult { get; set; }

        public ServiceRolloutCommandObservationSnapshot? GetServiceRolloutCommandObservationResult { get; set; }

        public ServiceTrafficViewSnapshot? GetServiceTrafficViewResult { get; set; }

        public ServiceIdentity? LastGetServiceIdentity { get; private set; }

        public ServiceIdentity? LastGetServiceRevisionsIdentity { get; private set; }

        public ServiceIdentity? LastGetServiceDeploymentsIdentity { get; private set; }

        public ServiceIdentity? LastGetServiceServingSetIdentity { get; private set; }

        public ServiceIdentity? LastGetServiceRolloutIdentity { get; private set; }

        public (ServiceIdentity identity, string commandId)? LastGetServiceRolloutCommandObservation { get; private set; }

        public ServiceIdentity? LastGetServiceTrafficViewIdentity { get; private set; }

        public int LastListServicesTake { get; private set; }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetServiceIdentity = identity;
            return Task.FromResult(GetServiceResult);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default)
        {
            LastListServicesTake = take;
            return Task.FromResult(ListServicesResult);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetServiceRevisionsIdentity = identity;
            return Task.FromResult(GetServiceRevisionsResult);
        }

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetServiceDeploymentsIdentity = identity;
            return Task.FromResult(GetServiceDeploymentsResult);
        }

        public Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetServiceServingSetIdentity = identity;
            return Task.FromResult(GetServiceServingSetResult);
        }

        public Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetServiceRolloutIdentity = identity;
            return Task.FromResult(GetServiceRolloutResult);
        }

        public Task<ServiceRolloutCommandObservationSnapshot?> GetServiceRolloutCommandObservationAsync(
            ServiceIdentity identity,
            string commandId,
            CancellationToken ct = default)
        {
            LastGetServiceRolloutCommandObservation = (identity, commandId);
            return Task.FromResult(GetServiceRolloutCommandObservationResult);
        }

        public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetServiceTrafficViewIdentity = identity;
            return Task.FromResult(GetServiceTrafficViewResult);
        }
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
                RequestId = "request-1",
                ServiceKey = "tenant/app/ns/orders",
                DeploymentId = "dep-1",
                TargetActorId = "target-actor",
                EndpointId = request.EndpointId,
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
                RunId = "run-1",
            });
        }
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

    private sealed class FakeServiceInvocationCatalogQueryReader(FakeServiceCatalogQueryReader catalogReader) : IServiceInvocationCatalogQueryReader
    {
        public Dictionary<string, ServiceInvocationCatalogSnapshot?> CatalogsByServiceKey { get; } = new(StringComparer.Ordinal);

        public ServiceInvocationCatalogSnapshot? Catalog { get; set; }

        public Task<ServiceInvocationCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            if (Catalog != null)
                return Task.FromResult<ServiceInvocationCatalogSnapshot?>(Catalog);

            var serviceKey = ServiceKeys.Build(identity);
            if (CatalogsByServiceKey.TryGetValue(serviceKey, out var configuredCatalog))
                return Task.FromResult(configuredCatalog);

            var service = catalogReader.Service;
            if (service == null)
                return Task.FromResult<ServiceInvocationCatalogSnapshot?>(null);

            var endpointId = service.Endpoints.FirstOrDefault()?.EndpointId;
            if (string.IsNullOrWhiteSpace(endpointId))
                endpointId = "chat";
            var revisionId = string.IsNullOrWhiteSpace(service.ActiveServingRevisionId)
                ? "rev-active"
                : service.ActiveServingRevisionId;
            var deploymentId = string.IsNullOrWhiteSpace(service.DeploymentId)
                ? "dep-1"
                : service.DeploymentId;
            var actorId = string.IsNullOrWhiteSpace(service.PrimaryActorId)
                ? "actor-1"
                : service.PrimaryActorId;
            return Task.FromResult<ServiceInvocationCatalogSnapshot?>(new ServiceInvocationCatalogSnapshot(
                serviceKey,
                [
                    new ServiceInvokeReadinessSnapshot(
                        serviceKey,
                        endpointId,
                        ServiceInvokeReadinessStatus.Ready,
                        ServiceInvokeUnavailableReason.Unspecified,
                        revisionId,
                        deploymentId,
                        actorId,
                        DateTimeOffset.UtcNow,
                        1,
                        $"{serviceKey}:invocation-catalog:1",
                        1,
                        1,
                        1),
                ],
                DateTimeOffset.UtcNow,
                1,
                $"{serviceKey}:invocation-catalog:1",
                1,
                1,
                1));
        }
    }

    private static ServiceInvocationCatalogSnapshot BuildInvocationCatalog(
        string serviceKey,
        string endpointId,
        ServiceInvokeReadinessStatus status,
        ServiceInvokeUnavailableReason reason,
        string revisionId,
        string deploymentId,
        string actorId,
        long stateVersion) =>
        new(
            serviceKey,
            [
                new ServiceInvokeReadinessSnapshot(
                    serviceKey,
                    endpointId,
                    status,
                    reason,
                    revisionId,
                    deploymentId,
                    actorId,
                    DateTimeOffset.Parse("2026-03-14T00:04:00+00:00"),
                    stateVersion,
                    $"evt-{stateVersion}",
                    stateVersion - 2,
                    stateVersion - 1,
                    stateVersion),
            ],
            DateTimeOffset.Parse("2026-03-14T00:04:00+00:00"),
            stateVersion,
            $"evt-{stateVersion}",
            stateVersion - 2,
            stateVersion - 1,
            stateVersion);

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

    internal static ByteString BuildProtocolDescriptorSetFor(MessageDescriptor descriptor)
    {
        var fds = new FileDescriptorSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectFileProto(descriptor.File, fds, seen);
        return fds.ToByteString();
    }

    private static void CollectFileProto(FileDescriptor file, FileDescriptorSet fds, HashSet<string> seen)
    {
        if (!seen.Add(file.Name))
            return;
        foreach (var dep in file.Dependencies)
        {
            CollectFileProto(dep, fds, seen);
        }

        var fileProto = FileDescriptorProto.Parser.ParseFrom(file.SerializedData);
        fds.File.Add(fileProto);
    }

    private sealed class TestStaticAgent : Aevatar.Foundation.Abstractions.IAgent
    {
        public string Id => "test-static-agent";

        public Task HandleEventAsync(Aevatar.Foundation.Abstractions.EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("test");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
