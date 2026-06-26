using System.Reflection;
using System.Security.Claims;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowBoardSnapshotEndpointsTests
{
    private const string ScopeId = "scope-mainnet-01";

    [Fact]
    public void Map_ShouldExposeCanonicalRouteWithoutLegacyTeamsCompatibilityRoute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IWorkflowBoardSnapshotQueryPort, RecordingSnapshotQueryPort>();
        builder.Services.AddRouting();
        var app = builder.Build();

        WorkflowBoardSnapshotEndpoints.Map(app);

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        routes.Should().ContainSingle("/api/scopes/{scopeId}/workflow-board/snapshot");
        routes.Should().NotContain("/teams/{scopeId}/workflow-board/snapshot");
        routes.Should().NotContain("/api/teams/{scopeId}/workflow-board/snapshot");
    }

    [Fact]
    public async Task HandleSnapshotAsync_ShouldReturnForbiddenBeforeCallingService_WhenScopeDenied()
    {
        var service = new RecordingSnapshotQueryPort();

        var result = await InvokeHandle(
            CreateAuthenticatedContext("other-scope"),
            ScopeId,
            new WorkflowBoardSnapshotHttpRequest(
                [new WorkflowBoardTeamSelectionHttpRequest("t-alpha", ["m-alpha"])]),
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        service.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleSnapshotAsync_ShouldRejectInvalidRequestShape()
    {
        var cases = new WorkflowBoardSnapshotHttpRequest?[]
        {
            null,
            new(null),
            new([]),
            new([new WorkflowBoardTeamSelectionHttpRequest(null, ["m-alpha"])]),
            new([new WorkflowBoardTeamSelectionHttpRequest(" ", ["m-alpha"])]),
            new([new WorkflowBoardTeamSelectionHttpRequest("t-alpha", null)]),
            new([new WorkflowBoardTeamSelectionHttpRequest("t-alpha", [])]),
            new(
                Enumerable.Range(0, 5)
                    .Select(index => new WorkflowBoardTeamSelectionHttpRequest($"t-{index}", ["m-alpha"]))
                    .ToArray()),
            new([new WorkflowBoardTeamSelectionHttpRequest(
                "t-alpha",
                Enumerable.Range(0, 25).Select(index => $"m-{index}").ToArray())]),
            new([new WorkflowBoardTeamSelectionHttpRequest("t-alpha", ["m-alpha"])], " "),
            new([new WorkflowBoardTeamSelectionHttpRequest("t-alpha", ["m-alpha"])], new string('w', 257)),
        };

        foreach (var request in cases)
        {
            var service = new RecordingSnapshotQueryPort();
            var result = await InvokeHandle(
                CreateAuthenticatedContext(ScopeId),
                ScopeId,
                request,
                service,
                CancellationToken.None);

            GetStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
            GetBodyProperty<string>(result, "code").Should().Be("INVALID_WORKFLOW_BOARD_SNAPSHOT_REQUEST");
            service.Requests.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task HandleSnapshotAsync_ShouldMapApplicationSnapshotToLowercaseHttpResponse()
    {
        var appSnapshot = new WorkflowBoardSnapshot(
            ScopeId,
            DateTimeOffset.Parse("2026-06-24T13:24:16Z"),
            "workflow-board:v1:selection:facts",
            new WorkflowBoardTotals(null, null, null, null),
            [
                new WorkflowBoardTeamSnapshot(
                    "t-alpha",
                    "Alpha",
                    8,
                    1,
                    [
                        new WorkflowBoardMemberSnapshot(
                            "m-alpha",
                            "Alpha member",
                            WorkflowBoardExecutionAvailability.PendingBackendContract,
                            [],
                            [new WorkflowBoardPendingNode("node-pending", "Pending", WorkflowBoardPendingNodeStatus.Pending)],
                            [])
                        {
                            WorkflowId = "wf-alpha",
                            WorkflowName = "Workflow Alpha",
                            PublishedServiceId = "svc-alpha",
                            ActorId = "actor-alpha",
                            RoleSummary = "role alpha",
                            CurrentNode = new WorkflowBoardCurrentNode(
                                "node-current",
                                "Current",
                                WorkflowBoardCurrentNodeStatus.Running,
                                DateTimeOffset.Parse("2026-06-24T13:20:00Z"),
                                DateTimeOffset.Parse("2026-06-24T13:24:00Z"),
                                240000),
                        },
                    ]),
            ],
            [
                new WorkflowBoardInvalidSelection(
                    "t-alpha",
                    "m-missing",
                    WorkflowBoardInvalidSelectionReason.MemberNotFound,
                    "Selected member is no longer available."),
            ],
            DateTimeOffset.Parse("2026-06-24T13:24:00Z"));
        var service = new RecordingSnapshotQueryPort(appSnapshot);

        var result = await InvokeHandle(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new WorkflowBoardSnapshotHttpRequest(
                [new WorkflowBoardTeamSelectionHttpRequest("t-alpha", ["m-alpha", "m-missing"])],
                "previous"),
            service,
            CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<WorkflowBoardSnapshotHttpResponse>>().Subject;
        service.Requests.Should().ContainSingle();
        service.Requests[0].ScopeId.Should().Be(ScopeId);
        service.Requests[0].PreviousWatermark.Should().Be("previous");
        ok.Value!.Teams.Should().ContainSingle();
        var member = ok.Value.Teams[0].Members.Should().ContainSingle().Subject;
        member.ExecutionAvailability.Should().Be("pending_backend_contract");
        member.CurrentNode!.Status.Should().Be("running");
        member.PendingNodes.Should().ContainSingle().Which.Status.Should().Be("pending");
        ok.Value.InvalidSelections.Should().ContainSingle()
            .Which.Reason.Should().Be("member_not_found");
        ok.Value.Totals.CompletedSteps.Should().BeNull();
    }

    [Fact]
    public async Task HandleSnapshotAsync_ShouldDedupeSelectedRowsBeforeEnforcingSelectedMemberLimit()
    {
        var service = new RecordingSnapshotQueryPort();
        var duplicateHeavyMembers = Enumerable.Range(0, 25)
            .Select(_ => "m-alpha")
            .ToArray();

        var result = await InvokeHandle(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new WorkflowBoardSnapshotHttpRequest(
                [new WorkflowBoardTeamSelectionHttpRequest("t-alpha", duplicateHeavyMembers)]),
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<WorkflowBoardSnapshotHttpResponse>>();
        service.Requests.Should().ContainSingle();
        service.Requests[0].TeamSelections.Should().ContainSingle();
        service.Requests[0].TeamSelections[0].MemberIds.Should().Equal("m-alpha");
    }

    [Fact]
    public async Task HandleSnapshotAsync_ShouldMapAllPublicLowercaseEnumValues()
    {
        var appSnapshot = new WorkflowBoardSnapshot(
            ScopeId,
            DateTimeOffset.Parse("2026-06-24T13:24:16Z"),
            "workflow-board:v1:selection:facts",
            new WorkflowBoardTotals(0, 0, 0, 0),
            [
                NewTeamSnapshot("t-available", "m-available", WorkflowBoardExecutionAvailability.Available),
                NewTeamSnapshot("t-unavailable", "m-unavailable", WorkflowBoardExecutionAvailability.Unavailable),
                NewTeamSnapshot(
                    "t-pending-backend-contract",
                    "m-pending-backend-contract",
                    WorkflowBoardExecutionAvailability.PendingBackendContract),
                NewTeamSnapshot("t-unknown", "m-unknown", WorkflowBoardExecutionAvailability.Unknown),
            ],
            [
                NewInvalidSelection(WorkflowBoardInvalidSelectionReason.TeamNotFound),
                NewInvalidSelection(WorkflowBoardInvalidSelectionReason.MemberNotFound),
                NewInvalidSelection(WorkflowBoardInvalidSelectionReason.MemberNotInTeam),
                NewInvalidSelection(WorkflowBoardInvalidSelectionReason.Unauthorized),
                NewInvalidSelection(WorkflowBoardInvalidSelectionReason.Archived),
                NewInvalidSelection(WorkflowBoardInvalidSelectionReason.Unknown),
            ]);
        var service = new RecordingSnapshotQueryPort(appSnapshot);

        var result = await InvokeHandle(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new WorkflowBoardSnapshotHttpRequest(
                [new WorkflowBoardTeamSelectionHttpRequest("t-alpha", ["m-alpha"])]),
            service,
            CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<WorkflowBoardSnapshotHttpResponse>>().Subject;
        ok.Value!.Teams
            .SelectMany(static team => team.Members)
            .Select(static member => member.ExecutionAvailability)
            .Should()
            .Equal("available", "unavailable", "pending_backend_contract", "unknown");
        ok.Value.Teams[0].Members[0].CurrentNode!.Status.Should().Be("running");
        ok.Value.Teams[1].Members[0].CurrentNode!.Status.Should().Be("waiting");
        ok.Value.Teams[2].Members[0].CurrentNode!.Status.Should().Be("pending");
        ok.Value.Teams[3].Members[0].CurrentNode!.Status.Should().Be("failed");
        ok.Value.Teams[0].Members[0].PendingNodes.Select(static node => node.Status)
            .Should()
            .Equal("waiting", "pending", "queued", "unknown");
        ok.Value.InvalidSelections.Select(static selection => selection.Reason)
            .Should()
            .Equal(
                "team_not_found",
                "member_not_found",
                "member_not_in_team",
                "unauthorized",
                "archived",
                "unknown");
    }

    [Fact]
    public async Task HandleSnapshotAsync_ShouldReturnBadGateway_WhenReadModelUnavailable()
    {
        var service = new RecordingSnapshotQueryPort(exception: new WorkflowBoardReadModelUnavailableException(
            "read model unavailable",
            new TimeoutException()));

        var result = await InvokeHandle(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new WorkflowBoardSnapshotHttpRequest(
                [new WorkflowBoardTeamSelectionHttpRequest("t-alpha", ["m-alpha"])]),
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status502BadGateway);
        GetBodyProperty<string>(result, "code").Should().Be("WORKFLOW_BOARD_SNAPSHOT_UNAVAILABLE");
    }

    private static async Task<IResult> InvokeHandle(
        HttpContext http,
        string scopeId,
        WorkflowBoardSnapshotHttpRequest? request,
        IWorkflowBoardSnapshotQueryPort service,
        CancellationToken ct)
    {
        var method = typeof(WorkflowBoardSnapshotEndpoints)
            .GetMethod("HandleSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HandleSnapshotAsync not found.");
        var task = (Task<IResult>)method.Invoke(null, [http, scopeId, request, service, ct])!;
        return await task;
    }

    private static HttpContext CreateAuthenticatedContext(string claimedScopeId)
    {
        var identity = new ClaimsIdentity([new Claim("scope_id", claimedScopeId)], "test");
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = services,
        };
    }

    private static int? GetStatusCode(IResult result) =>
        result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;

    private static T? GetBodyProperty<T>(IResult result, string propertyName)
    {
        var value = result.GetType().GetProperty("Value")?.GetValue(result);
        value.Should().NotBeNull();
        return (T?)value!.GetType().GetProperty(propertyName)?.GetValue(value);
    }

    private static WorkflowBoardTeamSnapshot NewTeamSnapshot(
        string teamId,
        string memberId,
        WorkflowBoardExecutionAvailability availability) =>
        new(
            teamId,
            teamId,
            1,
            1,
            [
                new WorkflowBoardMemberSnapshot(
                    memberId,
                    memberId,
                    availability,
                    [],
                    [
                        new WorkflowBoardPendingNode(
                            "pending-waiting",
                            "Waiting",
                            WorkflowBoardPendingNodeStatus.Waiting),
                        new WorkflowBoardPendingNode(
                            "pending-pending",
                            "Pending",
                            WorkflowBoardPendingNodeStatus.Pending),
                        new WorkflowBoardPendingNode(
                            "pending-queued",
                            "Queued",
                            WorkflowBoardPendingNodeStatus.Queued),
                        new WorkflowBoardPendingNode(
                            "pending-unknown",
                            "Unknown",
                            WorkflowBoardPendingNodeStatus.Unknown),
                    ],
                    [])
                {
                    CurrentNode = new WorkflowBoardCurrentNode(
                        "current",
                        "Current",
                        teamId switch
                        {
                            "t-available" => WorkflowBoardCurrentNodeStatus.Running,
                            "t-unavailable" => WorkflowBoardCurrentNodeStatus.Waiting,
                            "t-pending-backend-contract" => WorkflowBoardCurrentNodeStatus.Pending,
                            _ => WorkflowBoardCurrentNodeStatus.Failed,
                        }),
                },
            ]);

    private static WorkflowBoardInvalidSelection NewInvalidSelection(
        WorkflowBoardInvalidSelectionReason reason) =>
        new(
            "t-alpha",
            "m-alpha",
            reason,
            "invalid selection");

    private sealed class RecordingSnapshotQueryPort(
        WorkflowBoardSnapshot? snapshot = null,
        Exception? exception = null) : IWorkflowBoardSnapshotQueryPort
    {
        public List<WorkflowBoardSnapshotRequest> Requests { get; } = [];

        public Task<WorkflowBoardSnapshot> GetSnapshotAsync(
            WorkflowBoardSnapshotRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (exception != null)
                return Task.FromException<WorkflowBoardSnapshot>(exception);

            return Task.FromResult(snapshot ?? new WorkflowBoardSnapshot(
                request.ScopeId,
                DateTimeOffset.Parse("2026-06-24T13:24:16Z"),
                "workflow-board:v1:empty:empty",
                new WorkflowBoardTotals(null, null, null, null),
                [],
                []));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
