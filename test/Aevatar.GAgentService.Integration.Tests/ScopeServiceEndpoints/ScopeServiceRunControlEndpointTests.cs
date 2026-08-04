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
using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Integration.Tests;

[Collection(ScopeServiceEndpointCollection.Name)]
public sealed class ScopeServiceRunControlEndpointTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task ScopeResumeRunEndpoint_ShouldResolveScopedRunAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
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
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-1:resume", new
        {
            stepId = "approval-1",
            approved = true,
            userInput = "approved",
            toolApproval = new
            {
                executionId = "exec-default-1",
                toolCallId = "tool-call-default-1",
                approvalRequestId = "approval-default-1",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand!.ActorId.Should().Be("run-actor-default-1");
        host.ResumeDispatchService.LastCommand.RunId.Should().Be("run-default-1");
        host.ResumeDispatchService.LastCommand.StepId.Should().Be("approval-1");
        host.ResumeDispatchService.LastCommand.ToolApproval.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand.ToolApproval!.ExecutionId.Should().Be("exec-default-1");
        host.ResumeDispatchService.LastCommand.ToolApproval.ToolCallId.Should().Be("tool-call-default-1");
        host.ResumeDispatchService.LastCommand.ToolApproval.ApprovalRequestId.Should().Be("approval-default-1");
    }

    [Fact]
    public async Task ScopeResumeRunEndpoint_ShouldReturnConflict_WhenRunIsAmbiguous()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
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
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-2",
                "def-actor-2",
                "run-default-ambiguous",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
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
        body!["code"].Should().Be("SCOPE_RUN_AMBIGUOUS");
    }

    [Fact]
    public async Task ScopeResumeRunEndpoint_ShouldHonorRequestedActorIdFilter()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.RunBindingReader.BindingsByRunId["run-default-filtered"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-1",
                "def-actor-1",
                "run-default-filtered",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-2",
                "def-actor-2",
                "run-default-filtered",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-filtered:resume", new
        {
            stepId = "approval-1",
            approved = true,
            actorId = "run-actor-default-2",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand!.ActorId.Should().Be("run-actor-default-2");
        host.ResumeDispatchService.LastCommand.RunId.Should().Be("run-default-filtered");
    }

    [Fact]
    public async Task ScopeSignalRunEndpoint_ShouldResolveScopedRunAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
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
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-2:signal", new
        {
            signalName = "ops_window_open",
            stepId = "wait-1",
            payload = "window=open",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.SignalDispatchService.LastCommand.Should().NotBeNull();
        host.SignalDispatchService.LastCommand!.ActorId.Should().Be("run-actor-default-2");
        host.SignalDispatchService.LastCommand.RunId.Should().Be("run-default-2");
        host.SignalDispatchService.LastCommand.SignalName.Should().Be("ops_window_open");
    }

    [Fact]
    public async Task ScopeSignalRunEndpoint_ShouldHonorRequestedActorIdFilter()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
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
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-default-2",
                "def-actor-2",
                "run-default-2",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-2:signal", new
        {
            signalName = "ops_window_open",
            actorId = "run-actor-default-2",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.SignalDispatchService.LastCommand.Should().NotBeNull();
        host.SignalDispatchService.LastCommand!.ActorId.Should().Be("run-actor-default-2");
    }

    [Fact]
    public async Task ScopeStopRunEndpoint_ShouldResolveScopedRunAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
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
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-default-3:stop", new
        {
            reason = "manual",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.StopDispatchService.LastCommand.Should().NotBeNull();
        host.StopDispatchService.LastCommand!.ActorId.Should().Be("run-actor-default-3");
        host.StopDispatchService.LastCommand.RunId.Should().Be("run-default-3");
        host.StopDispatchService.LastCommand.Reason.Should().Be("manual");
    }

    [Fact]
    public async Task ScopeRetryCompensationRunEndpoint_ShouldResolveScopedRunAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.RunBindingReader.BindingsByRunId["run-dead-letter-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-dead-letter-1",
                "def-actor-1",
                "run-dead-letter-1",
                "main",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/runs/run-dead-letter-1:retry-compensation", new
        {
            failedCompensationStepId = "refund_payment",
            reason = "operator retry",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.RetryCompensationDispatchService.LastCommand.Should().NotBeNull();
        host.RetryCompensationDispatchService.LastCommand!.ActorId.Should().Be("run-actor-dead-letter-1");
        host.RetryCompensationDispatchService.LastCommand.RunId.Should().Be("run-dead-letter-1");
        host.RetryCompensationDispatchService.LastCommand.FailedCompensationStepId.Should().Be("refund_payment");
        host.RetryCompensationDispatchService.LastCommand.Reason.Should().Be("operator retry");
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
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-1:resume", new
        {
            stepId = "approval-1",
            approved = true,
            userInput = "approved",
            metadata = new Dictionary<string, string> { ["source"] = "test" },
            toolApproval = new
            {
                executionId = "exec-1",
                toolCallId = "tool-call-1",
                approvalRequestId = "approval-1",
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand!.ActorId.Should().Be("run-actor-1");
        host.ResumeDispatchService.LastCommand.RunId.Should().Be("run-1");
        host.ResumeDispatchService.LastCommand.StepId.Should().Be("approval-1");
        host.ResumeDispatchService.LastCommand.Approved.Should().BeTrue();
        host.ResumeDispatchService.LastCommand.ToolApproval.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand.ToolApproval!.ExecutionId.Should().Be("exec-1");
        host.ResumeDispatchService.LastCommand.ToolApproval.ToolCallId.Should().Be("tool-call-1");
        host.ResumeDispatchService.LastCommand.ToolApproval.ApprovalRequestId.Should().Be("approval-1");
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
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-2:signal", new
        {
            signalName = "ops_window_open",
            stepId = "wait-1",
            payload = "window=open",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
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
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-3:stop", new
        {
            reason = "manual",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.StopDispatchService.LastCommand.Should().NotBeNull();
        host.StopDispatchService.LastCommand!.ActorId.Should().Be("run-actor-3");
        host.StopDispatchService.LastCommand.RunId.Should().Be("run-3");
        host.StopDispatchService.LastCommand.Reason.Should().Be("manual");
    }

    [Fact]
    public async Task RetryCompensationRunEndpoint_ShouldResolveRunFromServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-dead-letter-2"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-dead-letter-2",
                "def-actor-1",
                "run-dead-letter-2",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-dead-letter-2:retry-compensation", new
        {
            failedCompensationStepId = "cancel_order",
            reason = "operator retry",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.RetryCompensationDispatchService.LastCommand.Should().NotBeNull();
        host.RetryCompensationDispatchService.LastCommand!.ActorId.Should().Be("run-actor-dead-letter-2");
        host.RetryCompensationDispatchService.LastCommand.RunId.Should().Be("run-dead-letter-2");
        host.RetryCompensationDispatchService.LastCommand.FailedCompensationStepId.Should().Be("cancel_order");
        host.RetryCompensationDispatchService.LastCommand.Reason.Should().Be("operator retry");
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
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-miss:resume", new
        {
            stepId = "approval-1",
            approved = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
