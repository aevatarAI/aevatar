using System.Net;
using System.Security.Claims;
using Aevatar.Audit.Hosting;
using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.Audit.Hosting.Tests;

public sealed class AuditTrailEndpointsTests
{
    private const string CallerScope = "scope-alice";
    private const string OtherScope = "scope-bob";

    [Fact]
    public async Task QueryAuditTrail_WhenScopeOmitted_UsesCallerScopeWithoutAdminAuthorization()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            http.RequestServices,
            NullLoggerFactory.Instance,
            scope: null,
            auditActorId: " audit_actor:abc ",
            take: 999);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        queryPort.Queries.Should().ContainSingle().Which.Should().BeEquivalentTo(new AuditTrailQuery(
            CallerScope,
            "audit_actor:abc",
            null,
            null,
            500,
            false));
        authorizer.Calls.Should().Be(0);
        body.Should().Contain("queryWatermark").And.Contain("readTimestampUtc").And.Contain("recordedAtUtc");
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndNonAdmin_DeniesBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            http.RequestServices,
            NullLoggerFactory.Instance,
            scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status403Forbidden);
        queryPort.Queries.Should().BeEmpty();
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndNonAdmin_DeniesBeforeQueryPortAvailability()
    {
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            http.RequestServices,
            NullLoggerFactory.Instance,
            scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status403Forbidden);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndMissingBearer_ReturnsUnauthorizedBeforeAuthorizer()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: null, queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            http.RequestServices,
            NullLoggerFactory.Instance,
            scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        queryPort.Queries.Should().BeEmpty();
        authorizer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndAdmin_ReadsTargetScope()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            http.RequestServices,
            NullLoggerFactory.Instance,
            scope: OtherScope,
            take: 10);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        queryPort.Queries.Should().ContainSingle().Which.Should().BeEquivalentTo(new AuditTrailQuery(
            OtherScope,
            null,
            null,
            null,
            10,
            true));
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenQueryPortMissing_ReturnsServiceUnavailable()
    {
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            http.RequestServices,
            NullLoggerFactory.Instance);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("AUDIT_QUERY_UNAVAILABLE");
    }

    [Fact]
    public async Task ResolveAuditActor_WhenNonAdmin_DeniesWithoutHashing()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildServiceProvider(queryPort: null, hasher, authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status403Forbidden);
        hasher.Identities.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenAdmin_ReturnsOnlyAuditActorId()
    {
        var hasher = new RecordingHasher { Hash = "audit_actor:hash" };
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildServiceProvider(queryPort: null, hasher, authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest(" nyxid ", "user@example.test"));
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        hasher.Identities.Should().ContainSingle().Which.Should().Be(new AuditExternalActorIdentity(
            "nyxid",
            "user@example.test"));
        body.Should().Contain("audit_actor:hash");
        body.Should().NotContain("user@example.test");
        body.Should().NotContain("nyxid");
    }

    [Fact]
    public async Task ResolveAuditActor_WhenBodyMissingIdentity_ReturnsBadRequest()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildServiceProvider(queryPort: null, hasher, authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", " "));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        hasher.Identities.Should().BeEmpty();
    }

    [Fact]
    public void DefaultAuditActorIdentityHasher_ShouldBeDeterministicAndNotExposeRawIdentity()
    {
        var hasher = new DefaultAuditActorIdentityHasher();

        var first = hasher.ComputeAuditActorId(new AuditExternalActorIdentity("NyxID", "user@example.test"));
        var second = hasher.ComputeAuditActorId(new AuditExternalActorIdentity("nyxid", "user@example.test"));

        first.Should().Be(second);
        first.Should().StartWith("audit_actor:");
        first.Should().NotContain("user@example.test");
        first.Should().NotContain("nyxid");
    }

    [Fact]
    public async Task AddAuditTrailCapabilityBundle_ShouldMapRoutesAndAdminMetadata()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.AddAuditTrailCapabilityBundle();

        await using var app = builder.Build();
        app.MapAevatarCapabilities();

        var routeEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        routeEndpoints.Select(static endpoint => endpoint.RoutePattern.RawText)
            .Should()
            .Contain(["/api/audit/trail", "/api/audit/actor-resolutions"]);
        routeEndpoints.Single(static endpoint => endpoint.RoutePattern.RawText == "/api/audit/trail")
            .Metadata
            .GetMetadata<AuditTrailEndpointAuditMetadata>()
            .Should()
            .BeEquivalentTo(new AuditTrailEndpointAuditMetadata("audit-trail", "query-cross-scope", "ADMIN"));
        routeEndpoints.Single(static endpoint => endpoint.RoutePattern.RawText == "/api/audit/actor-resolutions")
            .Metadata
            .GetMetadata<AuditTrailEndpointAuditMetadata>()
            .Should()
            .BeEquivalentTo(new AuditTrailEndpointAuditMetadata("audit-trail", "resolve-actor", "ADMIN"));
    }

    private static DefaultHttpContext BuildHttpContext(
        string scopeClaim,
        string? bearer,
        IAuditTrailQueryPort? queryPort,
        IPlatformAdminAuthorizer? authorizer = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = BuildServiceProvider(queryPort, hasher: null, authorizer),
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scope_id", scopeClaim)],
            authenticationType: "Test"));
        if (bearer is not null)
            context.Request.Headers.Authorization = $"Bearer {bearer}";

        return context;
    }

    private static IServiceProvider BuildServiceProvider(
        IAuditTrailQueryPort? queryPort,
        IAuditActorIdentityHasher? hasher,
        IPlatformAdminAuthorizer? authorizer)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        if (queryPort is not null)
            services.AddSingleton(queryPort);
        if (hasher is not null)
            services.AddSingleton(hasher);
        if (authorizer is not null)
            services.AddSingleton(authorizer);

        return services.BuildServiceProvider();
    }

    private static async Task<int> ExecuteAsync(IResult result, HttpContext http)
    {
        var (status, _) = await ExecuteWithBodyAsync(result, http);
        return status;
    }

    private static async Task<(int Status, string Body)> ExecuteWithBodyAsync(IResult result, HttpContext http)
    {
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        var body = await reader.ReadToEndAsync();
        return (http.Response.StatusCode, body);
    }

    private sealed class RecordingAuditTrailQueryPort : IAuditTrailQueryPort
    {
        public List<AuditTrailQuery> Queries { get; } = [];

        public Task<AuditTrailQueryResult> QueryAsync(
            AuditTrailQuery query,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(new AuditTrailQueryResult(
                [
                    new AuditTrailRecord(
                        "audit-1",
                        query.ScopeId,
                        query.AuditActorId ?? "audit_actor:default",
                        "READ",
                        "ALLOWED",
                        DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
                        DateTimeOffset.Parse("2026-01-02T03:04:06Z"),
                        ResourceType: "workflow",
                        ResourceId: "wf-1",
                        CorrelationId: "corr-1")
                ],
                DateTimeOffset.Parse("2026-01-02T03:04:07Z"),
                "projection:42"));
        }
    }

    private sealed class RecordingHasher : IAuditActorIdentityHasher
    {
        public string Hash { get; init; } = "audit_actor:test";

        public List<AuditExternalActorIdentity> Identities { get; } = [];

        public string ComputeAuditActorId(AuditExternalActorIdentity identity)
        {
            Identities.Add(identity);
            return Hash;
        }
    }

    private sealed class FakeAuthorizer : IPlatformAdminAuthorizer
    {
        private readonly bool _elevated;

        public FakeAuthorizer(bool elevated)
        {
            _elevated = elevated;
        }

        public int Calls { get; private set; }

        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_elevated
                ? new PlatformCaller(true, "admin", "admin@example.test", "admin-1")
                : PlatformCaller.NotElevated);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Audit.Hosting.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
