using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
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
using Aevatar.Workflow.Abstractions;
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
public sealed class ScopeServiceRunQueryEndpointTests : ScopeServiceEndpointTestKit
{
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
                ExternalCapabilityExecutionMode.Durable,
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
                ExternalCapabilityExecutionMode.Durable,
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
            SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
            DeadLetterFailedCompensationStepId = "refund_payment",
            DeadLetterRemainingUncompensated = 2,
            DeadLetterError = "refund failed",
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
        response.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        response.DeadLetter.Should().NotBeNull();
        response.DeadLetter!.FailedCompensationStepId.Should().Be("refund_payment");
        response.DeadLetter.RemainingUncompensated.Should().Be(2);
        response.DeadLetter.Error.Should().Be("refund failed");
    }

    [Fact]
    public async Task ListMemberRunsEndpoint_ShouldReturnMemberScopedRunHistory()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        host.LifecycleQueryPort.Service = BuildService("scope-a", "member-a", "def-member-active");
        host.LifecycleQueryPort.Deployments = new ServiceDeploymentCatalogSnapshot(
            "scope-a:default:default:member-a",
            [
                new ServiceDeploymentSnapshot("dep-member-active", "rev-2", "def-member-active", "Active", createdAt, updatedAt),
                new ServiceDeploymentSnapshot("dep-member-old", "rev-1", "def-member-old", "Inactive", createdAt.AddMinutes(-10), updatedAt.AddMinutes(-10)),
            ],
            updatedAt);
        host.RunBindingReader.BindingsByRunId["run-member-list-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-member-list-1",
                "def-member-old",
                "run-member-list-1",
                "member-flow",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a",
                CreatedAt: createdAt,
                UpdatedAt: updatedAt),
        ];
        host.WorkflowQueryService.SnapshotsByActorId["run-actor-member-list-1"] = new WorkflowActorSnapshot
        {
            ActorId = "run-actor-member-list-1",
            WorkflowName = "member-flow",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 13,
            LastEventId = "evt-13",
            LastUpdatedAt = updatedAt,
            LastSuccess = true,
            TotalSteps = 2,
            CompletedSteps = 2,
            RoleReplyCount = 1,
            LastOutput = "done",
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/scopes/scope-a/members/member-a/runs?take=5");
        request.Headers.Add("X-Test-Scope-Id", "scope-a");
        request.Headers.Add("X-Test-Member-Id", "member-a");

        var httpResponse = await host.Client.SendAsync(request);
        var response = await httpResponse.Content.ReadFromJsonAsync<ScopeServiceEndpoints.MemberScopeServiceRunCatalogHttpResponse>();

        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Should().NotBeNull();
        response!.ScopeId.Should().Be("scope-a");
        response.MemberId.Should().Be("member-a");
        response.PublishedServiceId.Should().Be("member-a");
        response.PublishedServiceKey.Should().Be("scope-a:default:default:member-a");
        response.Runs.Should().ContainSingle();
        response.Runs[0].RunId.Should().Be("run-member-list-1");
        response.Runs[0].MemberId.Should().Be("member-a");
        response.Runs[0].PublishedServiceId.Should().Be("member-a");
        response.Runs[0].DefinitionActorId.Should().Be("def-member-old");
        response.Runs[0].RevisionId.Should().Be("rev-1");
        response.Runs[0].DeploymentId.Should().Be("dep-member-old");
        response.Runs[0].StateVersion.Should().Be(13);
    }

    [Fact]
    public async Task GetMemberRunEndpoint_ShouldReturnMemberScopedRunSummary()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-7);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        host.LifecycleQueryPort.Service = BuildService("scope-a", "member-a", "def-member-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:member-a", "dep-member-1", "rev-1", "def-member-1");
        host.RunBindingReader.BindingsByRunId["run-member-detail-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-member-detail-1",
                "def-member-1",
                "run-member-detail-1",
                "member-flow",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a",
                CreatedAt: createdAt,
                UpdatedAt: updatedAt),
        ];
        host.WorkflowQueryService.SnapshotsByActorId["run-actor-member-detail-1"] = new WorkflowActorSnapshot
        {
            ActorId = "run-actor-member-detail-1",
            WorkflowName = "member-flow",
            CompletionStatus = WorkflowRunCompletionStatus.Running,
            StateVersion = 14,
            LastEventId = "evt-14",
            LastUpdatedAt = updatedAt,
            LastSuccess = null,
            TotalSteps = 3,
            CompletedSteps = 1,
            RoleReplyCount = 1,
            LastOutput = "working",
            SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
            DeadLetterFailedCompensationStepId = "cancel_order",
            DeadLetterRemainingUncompensated = 1,
            DeadLetterError = "cancel failed",
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/scopes/scope-a/members/member-a/runs/run-member-detail-1");
        request.Headers.Add("X-Test-Scope-Id", "scope-a");
        request.Headers.Add("X-Test-Member-Id", "member-a");

        var httpResponse = await host.Client.SendAsync(request);
        var response = await httpResponse.Content.ReadFromJsonAsync<ScopeServiceEndpoints.MemberScopeServiceRunSummaryHttpResponse>();

        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Should().NotBeNull();
        response!.ScopeId.Should().Be("scope-a");
        response.MemberId.Should().Be("member-a");
        response.PublishedServiceId.Should().Be("member-a");
        response.RunId.Should().Be("run-member-detail-1");
        response.ActorId.Should().Be("run-actor-member-detail-1");
        response.RevisionId.Should().Be("rev-1");
        response.WorkflowName.Should().Be("member-flow");
        response.StateVersion.Should().Be(14);
        response.LastEventId.Should().Be("evt-14");
        response.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        response.DeadLetter.Should().NotBeNull();
        response.DeadLetter!.FailedCompensationStepId.Should().Be("cancel_order");
        response.DeadLetter.RemainingUncompensated.Should().Be(1);
        response.DeadLetter.Error.Should().Be("cancel failed");
    }

    [Fact]
    public async Task GetMemberRunAuditEndpoint_ShouldReturnMemberScopedRunAuditReport()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-7);
        var updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        host.LifecycleQueryPort.Service = BuildService("scope-a", "member-a", "def-member-audit");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:member-a", "dep-member-audit", "rev-1", "def-member-audit");
        host.RunBindingReader.BindingsByRunId["run-member-audit-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-member-audit-1",
                "def-member-audit",
                "run-member-audit-1",
                "member-flow",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a",
                CreatedAt: createdAt,
                UpdatedAt: updatedAt),
        ];
        host.WorkflowQueryService.SnapshotsByActorId["run-actor-member-audit-1"] = new WorkflowActorSnapshot
        {
            ActorId = "run-actor-member-audit-1",
            WorkflowName = "member-flow",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            StateVersion = 15,
            LastEventId = "evt-15",
            LastUpdatedAt = updatedAt,
            LastSuccess = true,
            TotalSteps = 3,
            CompletedSteps = 3,
            RoleReplyCount = 1,
            LastOutput = "done",
        };
        host.WorkflowQueryService.ReportsByActorId["run-actor-member-audit-1"] = new WorkflowRunReport
        {
            WorkflowName = "member-flow",
            RootActorId = "run-actor-member-audit-1",
            StateVersion = 15,
            LastEventId = "evt-15",
            CompletionStatus = WorkflowRunCompletionStatus.Completed,
            ProjectionScope = WorkflowRunProjectionScope.RunIsolated,
            TopologySource = WorkflowRunTopologySource.CommittedProjection,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Success = true,
            FinalOutput = "done",
            Summary = new WorkflowRunStatistics
            {
                TotalSteps = 3,
                CompletedSteps = 3,
                RoleReplyCount = 1,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/scopes/scope-a/members/member-a/runs/run-member-audit-1/audit");
        request.Headers.Add("X-Test-Scope-Id", "scope-a");
        request.Headers.Add("X-Test-Member-Id", "member-a");

        var httpResponse = await host.Client.SendAsync(request);
        var response = await httpResponse.Content.ReadFromJsonAsync<ScopeServiceEndpoints.MemberScopeServiceRunAuditHttpResponse>();

        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Should().NotBeNull();
        response!.Summary.MemberId.Should().Be("member-a");
        response.Summary.PublishedServiceId.Should().Be("member-a");
        response.Summary.RunId.Should().Be("run-member-audit-1");
        response.Audit.RootActorId.Should().Be("run-actor-member-audit-1");
        host.WorkflowQueryService.ReportCalls.Should().ContainSingle("run-actor-member-audit-1");
    }

    [Fact]
    public async Task ResumeMemberRunEndpoint_ShouldResolveMemberPublishedServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "member-a", "def-member-resume");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:member-a", "dep-member-resume", "rev-1", "def-member-resume");
        host.RunBindingReader.BindingsByRunId["run-member-resume-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-member-resume-1",
                "def-member-resume",
                "run-member-resume-1",
                "member-flow",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/scopes/scope-a/members/member-a/runs/run-member-resume-1:resume",
            new
            {
                stepId = "approval-1",
                approved = true,
                toolApproval = new
                {
                    executionId = "exec-member-1",
                    toolCallId = "tool-call-member-1",
                    approvalRequestId = "approval-member-1",
                },
            },
            "scope-a");
        request.Headers.Add("X-Test-Member-Id", "member-a");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.ResumeDispatchService.LastCommand.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand!.ActorId.Should().Be("run-actor-member-resume-1");
        host.ResumeDispatchService.LastCommand.RunId.Should().Be("run-member-resume-1");
        host.ResumeDispatchService.LastCommand.StepId.Should().Be("approval-1");
        host.ResumeDispatchService.LastCommand.ToolApproval.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand.ToolApproval!.ExecutionId.Should().Be("exec-member-1");
        host.ResumeDispatchService.LastCommand.ToolApproval.ToolCallId.Should().Be("tool-call-member-1");
        host.ResumeDispatchService.LastCommand.ToolApproval.ApprovalRequestId.Should().Be("approval-member-1");
    }

    [Fact]
    public async Task SignalMemberRunEndpoint_ShouldResolveMemberPublishedServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "member-a", "def-member-signal");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:member-a", "dep-member-signal", "rev-1", "def-member-signal");
        host.RunBindingReader.BindingsByRunId["run-member-signal-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-member-signal-1",
                "def-member-signal",
                "run-member-signal-1",
                "member-flow",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/scopes/scope-a/members/member-a/runs/run-member-signal-1:signal",
            new
            {
                signalName = "ops_window_open",
                stepId = "wait-1",
            },
            "scope-a");
        request.Headers.Add("X-Test-Member-Id", "member-a");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.SignalDispatchService.LastCommand.Should().NotBeNull();
        host.SignalDispatchService.LastCommand!.ActorId.Should().Be("run-actor-member-signal-1");
        host.SignalDispatchService.LastCommand.RunId.Should().Be("run-member-signal-1");
        host.SignalDispatchService.LastCommand.SignalName.Should().Be("ops_window_open");
    }

    [Fact]
    public async Task StopMemberRunEndpoint_ShouldResolveMemberPublishedServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "member-a", "def-member-stop");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:member-a", "dep-member-stop", "rev-1", "def-member-stop");
        host.RunBindingReader.BindingsByRunId["run-member-stop-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-member-stop-1",
                "def-member-stop",
                "run-member-stop-1",
                "member-flow",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        using var request = CreateAuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/scopes/scope-a/members/member-a/runs/run-member-stop-1:stop",
            new
            {
                reason = "manual",
            },
            "scope-a");
        request.Headers.Add("X-Test-Member-Id", "member-a");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.StopDispatchService.LastCommand.Should().NotBeNull();
        host.StopDispatchService.LastCommand!.ActorId.Should().Be("run-actor-member-stop-1");
        host.StopDispatchService.LastCommand.RunId.Should().Be("run-member-stop-1");
        host.StopDispatchService.LastCommand.Reason.Should().Be("manual");
    }

    [Fact]
    public async Task RetryCompensationMemberRunEndpoint_ShouldResolveMemberPublishedServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "member-a", "def-member-retry");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:member-a", "dep-member-retry", "rev-1", "def-member-retry");
        host.RunBindingReader.BindingsByRunId["run-member-dead-letter-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-member-dead-letter-1",
                "def-member-retry",
                "run-member-dead-letter-1",
                "member-flow",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/members/member-a/runs/run-member-dead-letter-1:retry-compensation", new
        {
            failedCompensationStepId = "cancel_order",
            reason = "operator retry",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();
        host.RetryCompensationDispatchService.LastCommand.Should().NotBeNull();
        host.RetryCompensationDispatchService.LastCommand!.ActorId.Should().Be("run-actor-member-dead-letter-1");
        host.RetryCompensationDispatchService.LastCommand.RunId.Should().Be("run-member-dead-letter-1");
        host.RetryCompensationDispatchService.LastCommand.FailedCompensationStepId.Should().Be("cancel_order");
        host.RetryCompensationDispatchService.LastCommand.Reason.Should().Be("operator retry");
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
                ExternalCapabilityExecutionMode.Durable,
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
    }

    [Fact]
    public async Task ListRunsEndpoint_ShouldFilterByScheduleStatusAndUpdatedAt()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "static-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "static-actor-1");
        var baseTime = DateTimeOffset.Parse("2026-04-27T00:00:00+00:00");
        host.ServiceRunQueryPort.Upsert(BuildRunSnapshot(
            "scope-a",
            "orders",
            "run-match",
            "schedule-a",
            ServiceRunStatus.Completed,
            baseTime.AddHours(1)));
        host.ServiceRunQueryPort.Upsert(BuildRunSnapshot(
            "scope-a",
            "orders",
            "run-wrong-status",
            "schedule-a",
            ServiceRunStatus.Accepted,
            baseTime.AddHours(2)));
        host.ServiceRunQueryPort.Upsert(BuildRunSnapshot(
            "scope-a",
            "orders",
            "run-wrong-schedule",
            "schedule-b",
            ServiceRunStatus.Completed,
            baseTime.AddHours(3)));

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRunCatalogHttpResponse>(
            "/api/scopes/scope-a/services/orders/runs?take=1&scheduleId=schedule-a&status=completed&updatedFrom=2026-04-27T00:30:00Z&updatedTo=2026-04-27T01:30:00Z");

        response.Should().NotBeNull();
        response!.Runs.Select(x => x.RunId).Should().Equal("run-match");
        response.Runs[0].ScheduleId.Should().Be("schedule-a");
    }

    [Fact]
    public async Task ListMemberRunsEndpoint_ShouldApplyRegistryFilters()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "member-a", "def-member-registry");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:member-a", "dep-1", "rev-1", "def-member-registry");
        var baseTime = DateTimeOffset.Parse("2026-04-27T00:00:00+00:00");
        host.ServiceRunQueryPort.Upsert(BuildRunSnapshot(
            "scope-a",
            "member-a",
            "run-member-match",
            "schedule-member",
            ServiceRunStatus.Completed,
            baseTime.AddHours(1),
            ServiceImplementationKind.Workflow,
            "run-actor-member-registry"));
        host.RunBindingReader.BindingsByRunId["run-member-match"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-member-registry",
                "def-member-registry",
                "run-member-match",
                "member-flow",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Durable,
                "scope-a",
                CreatedAt: baseTime.AddMinutes(10),
                UpdatedAt: baseTime.AddHours(1)),
        ];
        host.ServiceRunQueryPort.Upsert(BuildRunSnapshot(
            "scope-a",
            "member-a",
            "run-member-skipped",
            "schedule-member",
            ServiceRunStatus.Accepted,
            baseTime.AddHours(2)));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/scopes/scope-a/members/member-a/runs?take=1&scheduleId=schedule-member&status=Completed&updatedFrom=2026-04-27T00:30:00Z&updatedTo=2026-04-27T01:30:00Z");
        request.Headers.Add("X-Test-Scope-Id", "scope-a");
        request.Headers.Add("X-Test-Member-Id", "member-a");

        var httpResponse = await host.Client.SendAsync(request);
        var response = await httpResponse.Content.ReadFromJsonAsync<ScopeServiceEndpoints.MemberScopeServiceRunCatalogHttpResponse>();

        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Should().NotBeNull();
        response!.Runs.Select(x => x.RunId).Should().Equal("run-member-match");
        response.Runs[0].ActorId.Should().Be("run-actor-member-registry");
        response.Runs[0].DefinitionActorId.Should().Be("def-member-registry");
        response.Runs[0].ScheduleId.Should().Be("schedule-member");
        response.Runs[0].CommandId.Should().Be("cmd-run-member-match");
        response.Runs[0].CorrelationId.Should().Be("corr-run-member-match");
        response.Runs[0].EndpointId.Should().Be("run");
        response.Runs[0].ImplementationKind.Should().Be(ServiceImplementationKind.Workflow.ToString());
        response.Runs[0].Status.Should().Be(ServiceRunStatus.Completed.ToString());
        response.Runs[0].CreatedAt.Should().Be(baseTime.AddMinutes(55));
    }

    [Fact]
    public async Task ListMemberRunsEndpoint_ShouldResolvePublishedServiceBeforeApplyingScheduleFilter()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.MemberPublishedServiceResolver.Result = new MemberPublishedServiceResolution(
            "scope-a",
            "m-alpha",
            "svc-alpha");
        host.LifecycleQueryPort.Service = BuildService("scope-a", "svc-alpha", "svc-alpha-actor");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:svc-alpha", "dep-1", "rev-1", "svc-alpha-actor");
        var baseTime = DateTimeOffset.Parse("2026-04-27T00:00:00+00:00");
        host.ServiceRunQueryPort.Upsert(BuildRunSnapshot(
            "scope-a",
            "svc-alpha",
            "run-schedule-a",
            "schedule-a",
            ServiceRunStatus.Completed,
            baseTime.AddHours(1)));
        host.ServiceRunQueryPort.Upsert(BuildRunSnapshot(
            "scope-a",
            "svc-alpha",
            "run-schedule-b",
            "schedule-b",
            ServiceRunStatus.Completed,
            baseTime.AddHours(2)));
        host.ServiceRunQueryPort.Upsert(BuildRunSnapshot(
            "scope-a",
            "m-alpha",
            "run-wrong-identity",
            "schedule-a",
            ServiceRunStatus.Completed,
            baseTime.AddHours(3)));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/scopes/scope-a/members/m-alpha/runs?take=10&scheduleId=schedule-a");
        request.Headers.Add("X-Test-Scope-Id", "scope-a");
        request.Headers.Add("X-Test-Member-Id", "m-alpha");

        var httpResponse = await host.Client.SendAsync(request);
        var response = await httpResponse.Content.ReadFromJsonAsync<ScopeServiceEndpoints.MemberScopeServiceRunCatalogHttpResponse>();

        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        host.MemberPublishedServiceResolver.Calls.Should().ContainSingle()
            .Which.Should().Be(new MemberPublishedServiceResolveRequest("scope-a", "m-alpha"));
        host.ServiceRunQueryPort.Queries.Should().ContainSingle()
            .Which.Should().Be(new ServiceRunQuery("scope-a", "svc-alpha", 10, "schedule-a"));
        response.Should().NotBeNull();
        response!.MemberId.Should().Be("m-alpha");
        response.PublishedServiceId.Should().Be("svc-alpha");
        response.Runs.Should().ContainSingle();
        response.Runs[0].RunId.Should().Be("run-schedule-a");
        response.Runs[0].MemberId.Should().Be("m-alpha");
        response.Runs[0].PublishedServiceId.Should().Be("svc-alpha");
        response.Runs[0].ScheduleId.Should().Be("schedule-a");
    }

    [Fact]
    public async Task ListRunsEndpoint_ShouldRejectInvalidFilterValues()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "static-actor-1");

        var statusResponse = await host.Client.GetAsync("/api/scopes/scope-a/services/orders/runs?status=unknown");
        var dateResponse = await host.Client.GetAsync("/api/scopes/scope-a/services/orders/runs?updatedFrom=not-a-date");

        statusResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        dateResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
                ExternalCapabilityExecutionMode.Durable,
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
        response.CompletionStatus.Should().Be(WorkflowRunCompletionStatus.Completed);
        response.LastOutput.Should().Be("done");
        response.LastSuccess.Should().BeTrue();
        response.StateVersion.Should().Be(8);
        response.LastEventId.Should().Be("evt-8");
        host.WorkflowQueryService.SnapshotCalls.Should().ContainSingle("workflow runs should still read current state from the workflow query service");
        host.WorkflowQueryService.SnapshotCalls[0].Should().Be("run-actor-orders-detail-1");
    }

    [Fact]
    public async Task GetRunEndpoint_ShouldReturnRegistryBackedSummaryForStaticService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "static-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "static-actor-1");

        var registration = await host.ServiceRunRegistrationPort.RegisterAsync(
            new ServiceRunRecord
            {
                ScopeId = "scope-a",
                ServiceId = "orders",
                ServiceKey = "scope-a:default:default:orders",
                RunId = "run-static-detail-1",
                CommandId = "cmd-static-1",
                CorrelationId = "corr-static-1",
                EndpointId = "chat",
                ImplementationKind = ServiceImplementationKind.Static,
                TargetActorId = "static-actor-1",
                RevisionId = "rev-1",
                DeploymentId = "dep-1",
                Status = ServiceRunStatus.Accepted,
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "orders",
                },
            },
            CancellationToken.None);
        await host.ServiceRunRegistrationPort.UpdateStatusAsync(
            registration.RunActorId,
            registration.RunId,
            ServiceRunStatus.Completed,
            "static result",
            null,
            CancellationToken.None);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRunSummaryHttpResponse>(
            "/api/scopes/scope-a/services/orders/runs/run-static-detail-1");

        response.Should().NotBeNull();
        response!.ScopeId.Should().Be("scope-a");
        response.ServiceId.Should().Be("orders");
        response.RunId.Should().Be("run-static-detail-1");
        response.ActorId.Should().Be("static-actor-1");
        response.ImplementationKind.Should().Be(ServiceImplementationKind.Static.ToString());
        response.Status.Should().Be(ServiceRunStatus.Completed.ToString());
        response.CompletionStatus.Should().Be(WorkflowRunCompletionStatus.Completed);
        response.LastOutput.Should().Be("static result");
        response.LastError.Should().BeEmpty();
        response.LastSuccess.Should().BeTrue();
        response.WorkflowName.Should().BeEmpty();
        host.WorkflowQueryService.SnapshotCalls.Should().BeEmpty("static runs should read persisted terminal facts from the service-run registry");
    }

    [Fact]
    public async Task GetRunEndpoint_ShouldReturnRegistryBackedSummaryForScriptingService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "script-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "script-actor-1");

        var registration = await host.ServiceRunRegistrationPort.RegisterAsync(
            new ServiceRunRecord
            {
                ScopeId = "scope-a",
                ServiceId = "orders",
                ServiceKey = "scope-a:default:default:orders",
                RunId = "run-script-detail-1",
                CommandId = "cmd-script-1",
                CorrelationId = "corr-script-1",
                EndpointId = "chat",
                ImplementationKind = ServiceImplementationKind.Scripting,
                TargetActorId = "script-actor-1",
                RevisionId = "rev-1",
                DeploymentId = "dep-1",
                Status = ServiceRunStatus.Accepted,
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "orders",
                },
            },
            CancellationToken.None);
        await host.ServiceRunRegistrationPort.UpdateStatusAsync(
            registration.RunActorId,
            registration.RunId,
            ServiceRunStatus.Failed,
            null,
            "script failed",
            CancellationToken.None);

        var response = await host.Client.GetFromJsonAsync<ScopeServiceEndpoints.ScopeServiceRunSummaryHttpResponse>(
            "/api/scopes/scope-a/services/orders/runs/run-script-detail-1");

        response.Should().NotBeNull();
        response!.ScopeId.Should().Be("scope-a");
        response.ServiceId.Should().Be("orders");
        response.RunId.Should().Be("run-script-detail-1");
        response.ActorId.Should().Be("script-actor-1");
        response.ImplementationKind.Should().Be(ServiceImplementationKind.Scripting.ToString());
        response.Status.Should().Be(ServiceRunStatus.Failed.ToString());
        response.CompletionStatus.Should().Be(WorkflowRunCompletionStatus.Failed);
        response.LastOutput.Should().BeEmpty();
        response.LastError.Should().Be("script failed");
        response.LastSuccess.Should().BeFalse();
        response.WorkflowName.Should().BeEmpty();
        host.WorkflowQueryService.SnapshotCalls.Should().BeEmpty("scripting runs should read persisted terminal facts from the service-run registry");
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
                ExternalCapabilityExecutionMode.Durable,
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
            TopologySource = WorkflowRunTopologySource.CommittedProjection,
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
                ExternalCapabilityExecutionMode.Durable,
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
            TopologySource = WorkflowRunTopologySource.CommittedProjection,
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

    private static ServiceRunSnapshot BuildRunSnapshot(
        string scopeId,
        string serviceId,
        string runId,
        string scheduleId,
        ServiceRunStatus status,
        DateTimeOffset updatedAt,
        ServiceImplementationKind implementationKind = ServiceImplementationKind.Static,
        string targetActorId = "static-actor-1") =>
        new(
            ScopeId: scopeId,
            ServiceId: serviceId,
            ServiceKey: $"{scopeId}:default:default:{serviceId}",
            RunId: runId,
            CommandId: $"cmd-{runId}",
            CorrelationId: $"corr-{runId}",
            EndpointId: "run",
            ScheduleId: scheduleId,
            ImplementationKind: implementationKind,
            TargetActorId: targetActorId,
            RevisionId: "rev-1",
            DeploymentId: "dep-1",
            Status: status,
            ActorId: $"service-run:{scopeId}:{serviceId}:{runId}",
            TenantId: scopeId,
            AppId: "default",
            Namespace: "default",
            StateVersion: 1,
            LastEventId: $"{runId}:registered",
            CreatedAt: updatedAt.AddMinutes(-5),
            UpdatedAt: updatedAt,
            LastOutput: string.Empty,
            LastError: string.Empty);
}
