using System.Net;
using System.Security.Claims;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Hosting;
using Aevatar.Authentication.Abstractions;
using Aevatar.Capabilities;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
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
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: null,
            auditActorId: " audit_actor:abc ",
            take: 999);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.ScopeId.Should().Be(CallerScope);
        query.AuditActorId.Should().Be("audit_actor:abc");
        query.IdentityKeyId.Should().BeNull();
        query.OccurredFrom.Should().BeNull();
        query.OccurredTo.Should().BeNull();
        query.Take.Should().Be(500);
        authorizer.Calls.Should().Be(0);
        body.Should().Contain("queryWatermark").And.Contain("readTimestampUtc").And.Contain("identityKeyId");
        body.Should().Contain("nextCursor");
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCallerScopeMissing_ReturnsUnauthorizedBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var http = BuildHttpContext(scopeClaim: null, bearer: "token", queryPort);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        queryPort.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCallerScopeAmbiguous_ReturnsUnauthorizedBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var http = BuildHttpContext(
            scopeClaim: null,
            bearer: "token",
            queryPort,
            scopeClaims:
            [
                new Claim("scope_id", CallerScope),
                new Claim("workflow.scope_id", OtherScope),
            ]);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        queryPort.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndNonAdmin_DeniesBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
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
            BuildEndpointDependencies(queryPort: null, authorizer: authorizer),
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
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        queryPort.Queries.Should().BeEmpty();
        authorizer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndAdminAuthorizerMissing_ReturnsUnavailableBeforeQuery()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance,
            scope: OtherScope);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("AUDIT_ADMIN_AUTH_UNAVAILABLE");
        queryPort.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryAuditTrail_WhenCrossScopeAndAdmin_ReadsTargetScope()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort, authorizer);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort, authorizer: authorizer),
            NullLoggerFactory.Instance,
            scope: OtherScope,
            take: 10);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.ScopeId.Should().Be(OtherScope);
        query.AuditActorId.Should().BeNull();
        query.Take.Should().Be(10);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenFiltersProvided_PreservesQueryFilters()
    {
        var queryPort = new RecordingAuditTrailQueryPort();
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort);
        var from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var to = DateTimeOffset.Parse("2026-01-31T23:59:59Z");

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort),
            NullLoggerFactory.Instance,
            auditActorId: " audit_actor:abc ",
            identityKeyId: " key-1 ",
            cursor: " cursor-1 ",
            from: from,
            to: to,
            take: 25);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.ScopeId.Should().Be(CallerScope);
        query.AuditActorId.Should().Be("audit_actor:abc");
        query.IdentityKeyId.Should().Be("key-1");
        query.Cursor.Should().Be("cursor-1");
        query.OccurredFrom.Should().Be(from);
        query.OccurredTo.Should().Be(to);
        query.Take.Should().Be(25);
    }

    [Fact]
    public async Task QueryAuditTrail_WhenQueryPortMissing_ReturnsServiceUnavailable()
    {
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.QueryAuditTrail(
            http,
            BuildEndpointDependencies(queryPort: null),
            NullLoggerFactory.Instance);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("AUDIT_QUERY_UNAVAILABLE");
    }

    [Fact]
    public async Task ResolveAuditActor_WhenCallerScopeMissing_ReturnsUnauthorizedBeforeAuthorization()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(scopeClaim: null, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        authorizer.Calls.Should().Be(0);
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenCallerScopeAmbiguous_ReturnsUnauthorizedBeforeAuthorization()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(
            scopeClaim: null,
            bearer: "token",
            queryPort: null,
            scopeClaims:
            [
                new Claim("scope_id", CallerScope),
                new Claim("workflow.scope_id", OtherScope),
            ]);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status401Unauthorized);
        authorizer.Calls.Should().Be(0);
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenNonAdmin_DeniesWithoutHashing()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status403Forbidden);
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenAdminAuthorizerMissing_ReturnsUnavailableBeforeHashing()
    {
        var hasher = new RecordingHasher();
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: null),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("AUDIT_ADMIN_AUTH_UNAVAILABLE");
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenHasherMissing_ReturnsUnavailableAfterAdminAuthorization()
    {
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: null, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "user@example.test"));
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.Should().Contain("AUDIT_ACTOR_HASHER_UNAVAILABLE");
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAuditActor_WhenAdmin_ReturnsOnlyAuditIdentity()
    {
        var hasher = new RecordingHasher
        {
            Identity = new AuditActorIdentity("audit_actor:hash", "key-1"),
        };
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest(" nyxid ", "user@example.test"));
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(StatusCodes.Status200OK);
        hasher.CanonicalActorKeys.Should().ContainSingle().Which.Should().Be("nyxid:user@example.test");
        body.Should().Contain("audit_actor:hash");
        body.Should().Contain("key-1");
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
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", " "));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        hasher.CanonicalActorKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAuditActor_WhenIdentityContainsColon_ReturnsBadRequestBeforeHashing()
    {
        var hasher = new RecordingHasher();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(CallerScope, bearer: "token", queryPort: null);

        var result = await AuditTrailEndpoints.ResolveAuditActor(
            http,
            BuildEndpointDependencies(hasher: hasher, authorizer: authorizer),
            NullLoggerFactory.Instance,
            new AuditActorResolutionRequest("nyxid", "scope:user"));
        var status = await ExecuteAsync(result, http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        hasher.CanonicalActorKeys.Should().BeEmpty();
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

    [Fact]
    public async Task AddAuditTrailCapabilityBundle_WhenQueryPortMissing_ReportsDegradedHealthContributor()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.AddAuditTrailCapabilityBundle();

        await using var app = builder.Build();

        var contributor = app.Services.GetServices<AevatarHealthContributorRegistration>()
            .Single(static registration => registration.Name == "audit-trail");
        var result = await contributor.ProbeAsync!(app.Services, CancellationToken.None);

        result.Status.Should().Be(AevatarHealthStatuses.Degraded);
        result.Message.Should().Be("Audit trail query port is not configured.");
    }

    private static DefaultHttpContext BuildHttpContext(
        string? scopeClaim,
        string? bearer,
        IAuditTrailQueryPort? queryPort,
        IPlatformAdminAuthorizer? authorizer = null,
        IReadOnlyCollection<Claim>? scopeClaims = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = BuildServiceProvider(queryPort, hasher: null, authorizer),
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(scopeClaims ?? BuildScopeClaims(scopeClaim), "Test"));
        if (bearer is not null)
            context.Request.Headers.Authorization = $"Bearer {bearer}";

        return context;
    }

    private static Claim[] BuildScopeClaims(string? scopeClaim) =>
        scopeClaim is null ? [] : [new Claim("scope_id", scopeClaim)];

    private static AuditTrailEndpointDependencies BuildEndpointDependencies(
        IAuditTrailQueryPort? queryPort = null,
        IAuditActorIdentityHasher? hasher = null,
        IPlatformAdminAuthorizer? authorizer = null) =>
        new(
            queryPort is null ? [] : [queryPort],
            authorizer is null ? [] : [authorizer],
            hasher is null ? [] : [hasher]);

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

        public Task<AuditTrailPage> QueryAsync(
            AuditTrailQuery query,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(new AuditTrailPage(
                [
                    new AuditRecord
                    {
                        AuditId = "audit-1",
                        ScopeId = query.ScopeId!,
                        AuditActorId = query.AuditActorId ?? "audit_actor:default",
                        IdentityKeyId = "key-1",
                        OperationName = "READ",
                        Outcome = AuditOutcome.Success,
                        OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
                        Target = new AuditTarget { Kind = "workflow", Id = "wf-1" },
                        Correlation = new AuditCorrelation { RequestId = "corr-1" },
                    }
                ],
                "cursor-2",
                DateTimeOffset.Parse("2026-01-02T03:04:07Z"),
                DateTimeOffset.Parse("2026-01-02T03:04:05Z")));
        }
    }

    private sealed class RecordingHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Identity { get; init; } = new("audit_actor:test", "key-test");

        public List<string> CanonicalActorKeys { get; } = [];

        public AuditActorIdentity Hash(string canonicalActorKey)
        {
            CanonicalActorKeys.Add(canonicalActorKey);
            return Identity;
        }

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId)
        {
            throw new NotSupportedException();
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
