using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.Audit;
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
public sealed class ScopeServiceBindingEndpointTests : ScopeServiceEndpointTestKit
{
    private const string RawToken = "eyJhbGciOiJVTklUIn0.eyJzdWIiOiJ1c2VyLTEyMyJ9.c2lnbmF0dXJlLXZhbHVl";

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldBindWorkflowBundleToDefaultService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PutAsJsonAsync("/api/scopes/scope-a/binding", new
        {
            implementationKind = "workflow",
            displayName = "Orders App",
            exposureDesired = true,
            workflow = new
            {
                workflowYamls = new[]
                {
                    "name: main\nsteps:\n  - run: echo hello",
                    "name: child\nsteps:\n  - run: echo child",
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ScopeBindingUpsertResult>();
        body.Should().NotBeNull();
        body!.AcceptanceStage.Should().Be("accepted");
        body.PropagationStage.Should().Be("readmodel_propagating");
        body.ExpectedActorId.Should().Be("scope-binding:expected-actor");
        host.ScopeBindingPort.LastRequest.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest!.ScopeId.Should().Be("scope-a");
        host.ScopeBindingPort.LastRequest.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        host.ScopeBindingPort.LastRequest.ExposureDesired.Should().BeTrue();
        host.ScopeBindingPort.LastRequest.Workflow.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest.Workflow!.WorkflowYamls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldAppendEndpointAuditRecord()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Put,
            $"/api/scopes/scope-a/binding?access_token={RawToken}",
            new
            {
                implementationKind = "scripting",
                script = new
                {
                    scriptId = "script-a",
                    scriptRevision = "script-rev-1",
                },
            },
            "scope-a");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RawToken);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.AuditTrailAppender.Records.Should().HaveCount(2);
        host.AuditTrailAppender.Records[0].OperationName.Should().Be("scope.binding.upsert.attempted");
        host.AuditTrailAppender.Records[0].Outcome.Should().Be(AuditOutcome.Accepted);
        host.AuditTrailAppender.Records[0].ResultSummary.Should().BeEmpty();
        host.AuditTrailAppender.Records[1].OperationName.Should().Be("scope.binding.upsert");
        host.AuditTrailAppender.Records[1].Outcome.Should().Be(AuditOutcome.Accepted);
        host.AuditTrailAppender.Records.Should().OnlyContain(record =>
            record.Target.Kind == "scope-binding" &&
            record.Target.Id == "scope-a" &&
            record.RequestSummary == "PUT /api/scopes/{scopeId}/binding scopeId=scope-a" &&
            record.CapturePlane == AuditCapturePlane.BoundaryEndpoint);
        host.AuditTrailAppender.Records.SelectMany(RecordStrings).Should().NotContain(value =>
            value.Contains(RawToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldIgnoreTopLevelWorkflowYamlsFallback()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PutAsJsonAsync("/api/scopes/scope-a/binding", new
        {
            implementationKind = "workflow",
            displayName = "Orders App",
            workflowYamls = new[]
            {
                "name: legacy\nsteps:\n  - run: echo legacy",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ScopeBindingPort.LastRequest.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest!.Workflow.Should().BeNull();
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
        var body = await response.Content.ReadFromJsonAsync<ScopeBindingUpsertResult>();
        body.Should().NotBeNull();
        body!.AcceptanceStage.Should().Be("accepted");
        body.PropagationStage.Should().Be("readmodel_propagating");
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
                agentKind = "tests.demo",
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
        var body = await response.Content.ReadFromJsonAsync<ScopeBindingUpsertResult>();
        body.Should().NotBeNull();
        body!.AcceptanceStage.Should().Be("accepted");
        body.PropagationStage.Should().Be("readmodel_propagating");
        host.ScopeBindingPort.LastRequest.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest!.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        host.ScopeBindingPort.LastRequest.GAgent.Should().NotBeNull();
        host.ScopeBindingPort.LastRequest.GAgent!.AgentKind.Should().Be("tests.demo");
        host.ScopeBindingPort.LastRequest.GAgent!.Endpoints.Should().ContainSingle();
        host.ScopeBindingPort.LastRequest.GAgent.Endpoints[0].EndpointId.Should().Be("run");
    }

    [Fact]
    public async Task ScopeBindingEndpoint_ShouldRejectLegacyGAgentActorTypeNameEvenWithAgentKind()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PutAsJsonAsync("/api/scopes/scope-a/binding", new
        {
            implementationKind = "gagent",
            gagent = new
            {
                agentKind = "tests.demo",
                actorTypeName = "Tests.DemoActor, Tests",
                endpoints = Array.Empty<object>(),
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("LEGACY_ACTOR_TYPE_NAME_REJECTED");
        host.ScopeBindingPort.LastRequest.Should().BeNull();
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

    private static IEnumerable<string> RecordStrings(AuditRecord record)
    {
        yield return record.AuditId;
        yield return record.ScopeId;
        yield return record.AuditActorId;
        yield return record.IdentityKeyId;
        yield return record.OperationName;
        yield return record.Target.Kind;
        yield return record.Target.Id;
        yield return record.Target.DisplayName;
        yield return record.Correlation.TraceId;
        yield return record.Correlation.RequestId;
        yield return record.Correlation.CommandId;
        yield return record.Correlation.CallId;
        yield return record.Correlation.SessionId;
        yield return record.Correlation.WorkflowRunId;
        yield return record.Correlation.ApprovalId;
        yield return record.RequestSummary;
        yield return record.ResultSummary;
        yield return record.ErrorCode;
        yield return record.ErrorSummary;
        foreach (var annotation in record.Annotations)
        {
            yield return annotation.Key;
            yield return annotation.Value;
        }
    }
}
