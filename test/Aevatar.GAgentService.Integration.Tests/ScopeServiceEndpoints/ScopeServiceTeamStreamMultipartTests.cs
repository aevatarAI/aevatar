using System.Net;
using Aevatar.AGUI.Contracts;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using ExternalCapabilityExecutionMode = Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode;
using WorkflowCapabilityAdmissionPlanIntegrity = Aevatar.Workflow.Abstractions.WorkflowCapabilityAdmissionPlanIntegrity;
using WorkflowToolCatalogPolicies = Aevatar.Workflow.Abstractions.WorkflowToolCatalogPolicies;

namespace Aevatar.GAgentService.Integration.Tests;

[Collection(ScopeServiceEndpointCollection.Name)]
public sealed class ScopeServiceTeamStreamMultipartTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task TeamInvokeStreamEndpoint_ShouldIngestMultipartFileForWorkflowTargetAfterTeamResolution()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.TeamEntryMemberResolver.Result = new TeamEntryMemberResolution(
            "scope-a",
            "team-a",
            "member-a",
            "member-a");
        var service = BuildService(
            "scope-a",
            "member-a",
            "definition-actor-member-a",
            "rev-team-member-a-1",
            "dep-team-member-a-1");
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
                            "dep-team-member-a-1",
                            "rev-team-member-a-1",
                            "definition-actor-member-a",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-team-member-a-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "member-a",
                },
                RevisionId = "rev-team-member-a-1",
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
                    WorkflowPlan = BuildInteractiveWorkflowPlan(
                        "member-a",
                        "name: member_a\nsteps:\n  - run: echo member",
                        "definition-actor-member-a"),
                },
            },
            CancellationToken.None);
        host.InteractionService.ResultFactory = async (request, emitAsync, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-team-a", "member-a", "cmd-team-a", "corr-team-a");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);
            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(WorkflowProjectionCompletionStatus.Completed, true));
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/teams/team-a/invoke/chat:stream")
        {
            Content = CreateMultipartScopeStreamContent(
                """
                {
                  "prompt": "hello team",
                  "headers": { "channel": "team-tests" }
                }
                """,
                [("cat.png", "image/png", "hello")]),
        };

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "stream body: {0}", body);
        body.Should().Contain("aevatar.run.context");
        host.TeamEntryMemberResolver.Calls.Should().ContainSingle().Which.Should().Be(("scope-a", "team-a", "chat"));
        host.WorkflowFileIngressPort.Requests.Should().ContainSingle();
        var ingressRequest = host.WorkflowFileIngressPort.Requests[0];
        ingressRequest.SourceKind.Should().Be(FileArtifactSourceKind.FormUpload);
        ingressRequest.OwnerScopeId.Should().Be("scope-a");
        ingressRequest.FileName.Should().Be("cat.png");
        ingressRequest.MediaType.Should().Be("image/png");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.Source.ActorId.Should().Be("definition-actor-member-a");
        host.InteractionService.LastRequest.Headers.Should().ContainKey("channel").WhoseValue.Should().Be("team-tests");
        var part = host.InteractionService.LastRequest.InputParts.Should().ContainSingle().Which;
        part.Kind.Should().Be(WorkflowChatInputPartKind.Image);
        part.DataBase64.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.ArtifactId.Should().Be("workflow-file://file-1");
        part.FileRef.OwnerScopeId.Should().Be("scope-a");
    }

    [Fact]
    public async Task TeamInvokeStreamEndpoint_ShouldRejectMultipartForStaticTargetWithoutWritingArtifacts()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.TeamEntryMemberResolver.Result = new TeamEntryMemberResolution(
            "scope-a",
            "team-a",
            "member-a",
            "member-a");
        var service = BuildService(
            "scope-a",
            "member-a",
            "definition-actor-member-a",
            "rev-team-member-a-1",
            "dep-team-member-a-1");
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
                            "dep-team-member-a-1",
                            "rev-team-member-a-1",
                            "definition-actor-member-a",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
        await host.RevisionCatalog.UpsertRevisionAsync(
            service.ServiceKey,
            "rev-team-member-a-1",
            new PreparedServiceRevisionArtifact
            {
                Identity = new ServiceIdentity
                {
                    TenantId = "scope-a",
                    AppId = "default",
                    Namespace = "default",
                    ServiceId = "member-a",
                },
                RevisionId = "rev-team-member-a-1",
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
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    StaticPlan = new StaticServiceDeploymentPlan
                    {
                        ActorTypeName = "Test.StaticAgent, Tests",
                    },
                },
            },
            CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/teams/team-a/invoke/chat:stream")
        {
            Content = CreateMultipartScopeStreamContent(
                """
                {
                  "prompt": "hello team"
                }
                """,
                [("cat.png", "image/png", "hello")]),
        };

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "stream body: {0}", body);
        body.Should().Contain("INVALID_SERVICE_STREAM_REQUEST");
        body.Should().Contain("Multipart file input is only supported for workflow services.");
        host.TeamEntryMemberResolver.Calls.Should().ContainSingle().Which.Should().Be(("scope-a", "team-a", "chat"));
        host.WorkflowFileIngressPort.Requests.Should().BeEmpty();
        host.StaticGAgentStreamInvocationPort.Requests.Should().BeEmpty();
        host.InteractionService.LastRequest.Should().BeNull();
    }

    private static WorkflowServiceDeploymentPlan BuildInteractiveWorkflowPlan(
        string workflowName,
        string workflowYaml,
        string definitionActorId)
    {
        const ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive;
        return new WorkflowServiceDeploymentPlan
        {
            ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
            WorkflowName = workflowName,
            WorkflowYaml = workflowYaml,
            DefinitionActorId = definitionActorId,
            CapabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
                workflowYaml,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                executionMode,
                [],
                []),
            ExecutionMode = executionMode,
        };
    }
}
