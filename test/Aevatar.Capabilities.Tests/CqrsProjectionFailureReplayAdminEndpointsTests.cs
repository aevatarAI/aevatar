using System.Security.Claims;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Authentication.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Mainnet.Host.Api.Cqrs;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class CqrsProjectionFailureReplayAdminEndpointsTests
{
    [Fact]
    public void Route_ShouldRequireAuthorizationAndRestrictedAudit()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        app.MapCqrsProjectionFailureRepairAdminEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(static candidate =>
                candidate.RoutePattern.RawText == CqrsProjectionFailureReplayAdminEndpoints.Route);
        endpoint.Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull();
        var audit = endpoint.Metadata.GetMetadata<EndpointAuditMetadata>();
        audit.Should().NotBeNull();
        audit!.OperationName.Should().Be("cqrs.projection-failures.replay-exhausted");
        audit.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
    }

    [Fact]
    public async Task HandleAsync_WhenRepairIsAccepted_ShouldReturnDispatchOnlyReceipt()
    {
        var repair = new RecordingRepairService(Result(
            ProjectionRetryExhaustedFailureRepairStatus.AcceptedForDispatch));
        var http = BuildHttpContext();

        var result = await CqrsProjectionFailureReplayAdminEndpoints.HandleAsync(
            http,
            ScopeActorId,
            ValidRequest(),
            ElevatedAuthorizer(),
            repair,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        repair.Requests.Should().ContainSingle();
        var request = repair.Requests[0];
        request.ScopeActorId.Should().Be(ScopeActorId);
        request.ExpectedScopeStateVersion.Should().Be(8343);
        request.ExpectedUnresolvedFailureCount.Should().Be(19);
        request.ExpectedRetryExhaustedFailureCount.Should().Be(19);
        request.MaxItems.Should().Be(19);
        request.RequestId.Should().Be("operator-replay-alpha");
        request.Reason.Should().Be("storage recovery completed");
        request.RequestedBySubjectId.Should().Be("admin-alpha");
    }

    [Theory]
    [InlineData(ProjectionRetryExhaustedFailureRepairStatus.ScopeNotActive)]
    [InlineData(ProjectionRetryExhaustedFailureRepairStatus.ScopeIdentityInvalid)]
    [InlineData(ProjectionRetryExhaustedFailureRepairStatus.ScopeIdentityMismatch)]
    [InlineData(ProjectionRetryExhaustedFailureRepairStatus.ManifestChanged)]
    [InlineData(ProjectionRetryExhaustedFailureRepairStatus.RecoveryIdentityUnavailable)]
    public async Task HandleAsync_WhenRepairConflicts_ShouldReturnConflict(
        ProjectionRetryExhaustedFailureRepairStatus status)
    {
        var repair = new RecordingRepairService(Result(status));

        var result = await CqrsProjectionFailureReplayAdminEndpoints.HandleAsync(
            BuildHttpContext(),
            ScopeActorId,
            ValidRequest(),
            ElevatedAuthorizer(),
            repair,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        repair.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WhenRepairRejectsRequest_ShouldReturnBadRequest()
    {
        var repair = new RecordingRepairService(Result(
            ProjectionRetryExhaustedFailureRepairStatus.InvalidRequest));

        var result = await CqrsProjectionFailureReplayAdminEndpoints.HandleAsync(
            BuildHttpContext(),
            ScopeActorId,
            ValidRequest(),
            ElevatedAuthorizer(),
            repair,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        repair.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WhenCallerIsNotElevated_ShouldFailClosedBeforeReadOrDispatch()
    {
        var repair = new RecordingRepairService(Result(
            ProjectionRetryExhaustedFailureRepairStatus.AcceptedForDispatch));

        var result = await CqrsProjectionFailureReplayAdminEndpoints.HandleAsync(
            BuildHttpContext(),
            ScopeActorId,
            ValidRequest(),
            new StubAuthorizer(PlatformCaller.NotElevated),
            repair,
            CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>();
        repair.Requests.Should().BeEmpty();
    }

    private static DefaultHttpContext BuildHttpContext()
    {
        var host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        });
        host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:Authentication:Enabled"] = "true",
        });
        var requestServices = host.Services.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = requestServices,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "admin-alpha"),
                new Claim("scope_id", "platform"),
            ], "Test")),
        };
        http.Request.Headers.Authorization = "Bearer test-token";
        return http;
    }

    private static IPlatformAdminAuthorizer ElevatedAuthorizer() =>
        new StubAuthorizer(new PlatformCaller(
            true,
            "operator",
            "admin@example.com",
            "admin-alpha",
            PlatformAdminGrantSources.NyxIdPlatformRole));

    private const string ScopeActorId =
        "projection.durable.scope:workflow-execution-materialization:workflow-run-alpha";

    private static ProjectionRetryExhaustedFailureRepairResult Result(
        ProjectionRetryExhaustedFailureRepairStatus status) =>
        new(
            status,
            ScopeActorId,
            "operator-replay-alpha",
            CurrentScopeStateVersion: 8343,
            CurrentUnresolvedFailureCount: 19,
            CurrentRetryExhaustedFailureCount: 19,
            MaxItems: 19);

    private static CqrsProjectionFailureReplayAdminEndpoints.ReplayRetryExhaustedFailuresRequest
        ValidRequest() =>
        new(
            ExpectedScopeStateVersion: 8343,
            ExpectedUnresolvedFailureCount: 19,
            ExpectedRetryExhaustedFailureCount: 19,
            MaxItems: 19,
            RequestId: "operator-replay-alpha",
            Reason: "storage recovery completed");

    private sealed class StubAuthorizer(PlatformCaller caller) : IPlatformAdminAuthorizer
    {
        public Task<PlatformCaller> ResolveCallerAsync(
            string bearerToken,
            CancellationToken ct = default) => Task.FromResult(caller);
    }

    private sealed class RecordingRepairService(
        ProjectionRetryExhaustedFailureRepairResult result)
        : IProjectionRetryExhaustedFailureRepairService
    {
        public List<ProjectionRetryExhaustedFailureRepairRequest> Requests { get; } = [];

        public Task<ProjectionRetryExhaustedFailureRepairResult> RepairAsync(
            ProjectionRetryExhaustedFailureRepairRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
