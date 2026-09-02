using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
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
            new WorkflowBoardSnapshotHttpRequest(TeamId: "t-alpha", MemberId: "m-alpha"),
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        service.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleSnapshotAsync_ShouldMapFilterRequestToApplicationRequest()
    {
        var service = new RecordingSnapshotQueryPort();

        var result = await InvokeHandle(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new WorkflowBoardSnapshotHttpRequest(
                TeamId: "t-protocol",
                MemberId: "m-alpha",
                Take: 20),
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<WorkflowBoardSnapshotHttpResponse>>();
        service.Requests.Should().ContainSingle()
            .Which.Should().Be(new WorkflowBoardSnapshotRequest(
                ScopeId,
                TeamId: "t-protocol",
                MemberId: "m-alpha",
                Take: 20));
    }

    [Fact]
    public async Task HandleSnapshotAsync_ShouldRejectInvalidRequestShape()
    {
        var cases = new WorkflowBoardSnapshotHttpRequest?[]
        {
            null,
            new(TeamId: " "),
            new(MemberId: "m-alpha"),
            new(TeamId: "t-alpha", MemberId: " "),
            new(TeamId: "t-alpha", Take: 0),
            new(TeamId: "t-alpha", Take: -1),
            new(TeamId: "t-alpha", Take: WorkflowBoardSnapshotRequestLimits.MaxMemberRows + 1),
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

    [Theory]
    [InlineData("teamSelections")]
    [InlineData("previousWatermark")]
    public async Task HandleSnapshotAsync_ShouldRejectLegacySelectedSnapshotFields(string legacyField)
    {
        using var json = JsonDocument.Parse("{\"value\":true}");
        var request = new WorkflowBoardSnapshotHttpRequest
        {
            ExtensionData = new Dictionary<string, JsonElement>
            {
                [legacyField] = json.RootElement.GetProperty("value").Clone(),
            },
        };
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

    [Fact]
    public async Task HandleSnapshotAsync_ShouldMapApplicationSnapshotToLowercaseHttpResponse()
    {
        var appSnapshot = new WorkflowBoardSnapshot(
            ScopeId,
            DateTimeOffset.Parse("2026-06-24T13:24:16Z"),
            "workflow-board:v2:filter:facts",
            new WorkflowBoardSnapshotCounts(
                Running: 1,
                Waiting: 2,
                Failed: 3,
                Retrying: 4,
                Completed: 5),
            [
                new WorkflowBoardTeamSnapshot(
                    "t-alpha",
                    "Alpha",
                    8,
                    [
                        new WorkflowBoardMemberSnapshot(
                            "m-alpha",
                            "Alpha member",
                            WorkflowBoardExecutionAvailability.Available,
                            [new WorkflowBoardCompletedNode("node-done", "Done")],
                            [new WorkflowBoardPendingNode("node-pending", "Pending", WorkflowBoardPendingNodeStatus.Pending)],
                            [new WorkflowBoardFailedNode("node-failed", "Failed")])
                        {
                            WorkflowId = "wf-alpha",
                            WorkflowName = "Workflow Alpha",
                            PublishedServiceId = "svc-alpha",
                            ActorId = "actor-alpha",
                            RoleSummary = "role alpha",
                            CurrentExecutionId = "run-alpha",
                            ExecutionStatus = WorkflowBoardMemberExecutionStatus.Running,
                            Progress = new WorkflowBoardMemberProgress(3, 8),
                            CurrentNode = new WorkflowBoardCurrentNode(
                                "node-current",
                                "Current",
                                WorkflowBoardCurrentNodeStatus.Running,
                                DateTimeOffset.Parse("2026-06-24T13:20:00Z"),
                                DateTimeOffset.Parse("2026-06-24T13:24:00Z"),
                                240000),
                            LastNodeUpdatedAt = DateTimeOffset.Parse("2026-06-24T13:24:00Z"),
                        },
                    ]),
            ],
            DateTimeOffset.Parse("2026-06-24T13:24:00Z"));
        var service = new RecordingSnapshotQueryPort(appSnapshot);

        var result = await InvokeHandle(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new WorkflowBoardSnapshotHttpRequest(TeamId: "t-alpha", MemberId: "m-alpha"),
            service,
            CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<WorkflowBoardSnapshotHttpResponse>>().Subject;
        ok.Value!.Counts.Should().Be(new WorkflowBoardSnapshotCountsHttpResponse(1, 2, 3, 4, 5));
        ok.Value.Teams.Should().ContainSingle();
        var member = ok.Value.Teams[0].Members.Should().ContainSingle().Subject;
        member.ExecutionAvailability.Should().Be("available");
        member.ExecutionStatus.Should().Be("running");
        member.Progress.Should().Be(new WorkflowBoardMemberProgressHttpResponse(3, 8));
        member.CurrentNode!.Status.Should().Be("running");
        member.PendingNodes.Should().ContainSingle().Which.Status.Should().Be("pending");
        member.WorkflowId.Should().Be("wf-alpha");
        member.PublishedServiceId.Should().Be("svc-alpha");
        member.CurrentExecutionId.Should().Be("run-alpha");
    }

    [Fact]
    public async Task HandleSnapshotAsync_ShouldMapAllPublicLowercaseExecutionStatusValues()
    {
        var appSnapshot = new WorkflowBoardSnapshot(
            ScopeId,
            DateTimeOffset.Parse("2026-06-24T13:24:16Z"),
            "workflow-board:v2:filter:facts",
            new WorkflowBoardSnapshotCounts(1, 1, 1, 1, 1),
            [
                NewTeamSnapshot("t-running", "m-running", WorkflowBoardMemberExecutionStatus.Running),
                NewTeamSnapshot("t-waiting", "m-waiting", WorkflowBoardMemberExecutionStatus.Waiting),
                NewTeamSnapshot("t-failed", "m-failed", WorkflowBoardMemberExecutionStatus.Failed),
                NewTeamSnapshot("t-retrying", "m-retrying", WorkflowBoardMemberExecutionStatus.Retrying),
                NewTeamSnapshot("t-completed", "m-completed", WorkflowBoardMemberExecutionStatus.Completed),
                NewTeamSnapshot("t-stopped", "m-stopped", WorkflowBoardMemberExecutionStatus.Stopped),
                NewTeamSnapshot("t-unknown", "m-unknown", WorkflowBoardMemberExecutionStatus.Unknown),
            ]);
        var service = new RecordingSnapshotQueryPort(appSnapshot);

        var result = await InvokeHandle(
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new WorkflowBoardSnapshotHttpRequest(),
            service,
            CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<WorkflowBoardSnapshotHttpResponse>>().Subject;
        ok.Value!.Teams
            .SelectMany(static team => team.Members)
            .Select(static member => member.ExecutionStatus)
            .Should()
            .Equal("running", "waiting", "failed", "retrying", "completed", "stopped", "unknown");
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
            new WorkflowBoardSnapshotHttpRequest(TeamId: "t-alpha", MemberId: "m-alpha"),
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
        WorkflowBoardMemberExecutionStatus status) =>
        new(
            teamId,
            teamId,
            1,
            [
                new WorkflowBoardMemberSnapshot(
                    memberId,
                    memberId,
                    WorkflowBoardExecutionAvailability.Available,
                    [],
                    [],
                    [])
                {
                    ExecutionStatus = status,
                },
            ]);

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
                "workflow-board:v2:empty:empty",
                new WorkflowBoardSnapshotCounts(0, 0, 0, 0, 0),
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
