using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Authentication.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Host.Api.Tests;

// 06-20-observatory-admin-cross-scope (G2): the endpoint auth matrix is the security crux — a non-admin must
// never reach a cross-scope query, and admin status comes only from the server-side authorizer.
public sealed class WorkflowRunObservatoryEndpointsAdminTests
{
    private const string OwnScope = "scope-alice";
    private const string OtherScope = "scope-bob";
    private const string RawToken = "eyJhbGciOiJVTklUIn0.eyJzdWIiOiJ1c2VyLTEyMyJ9.c2lnbmF0dXJlLXZhbHVl";

    [Fact]
    public async Task ListRuns_NoScope_UsesOwnScope_AndNeverCallsAuthorizerOrAdminQuery()
    {
        var observatory = new FakeObservatory();
        var adminQuery = new FakeAdminQuery();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.ListRuns(
            http, observatory, adminQuery, authorizer, NullLoggerFactory.Instance, scope: null);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(200);
        observatory.ListScopes.Should().ContainSingle().Which.Should().Be(OwnScope);
        adminQuery.ListAllCalls.Should().Be(0);
        authorizer.Calls.Should().Be(0); // own scope => no NyxID round-trip
    }

    [Fact]
    public async Task ListRuns_ScopeEqualsOwn_IsTreatedAsOwn_NoAuthorizer()
    {
        var observatory = new FakeObservatory();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        await WorkflowRunObservatoryEndpoints.ListRuns(
            http, observatory, new FakeAdminQuery(), authorizer, NullLoggerFactory.Instance, scope: OwnScope);

        observatory.ListScopes.Should().ContainSingle().Which.Should().Be(OwnScope);
        authorizer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ListRuns_CrossScope_NonAdmin_Denied_AndNeverQueries()
    {
        var observatory = new FakeObservatory();
        var adminQuery = new FakeAdminQuery();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.ListRuns(
            http, observatory, adminQuery, authorizer, NullLoggerFactory.Instance, scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(403);
        // The cross-scope query MUST NOT have been reached.
        observatory.ListScopes.Should().BeEmpty();
        adminQuery.ListAllCalls.Should().Be(0);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ListRuns_CrossScope_MissingBearer_Returns401_BeforeAuthorizer()
    {
        var observatory = new FakeObservatory();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: null);

        var result = await WorkflowRunObservatoryEndpoints.ListRuns(
            http, observatory, new FakeAdminQuery(), authorizer, NullLoggerFactory.Instance, scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(401);
        observatory.ListScopes.Should().BeEmpty();
        authorizer.Calls.Should().Be(0); // no token => no NyxID call
    }

    [Fact]
    public async Task ListRuns_CrossScope_Admin_QueriesTargetScope()
    {
        var observatory = new FakeObservatory();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        await WorkflowRunObservatoryEndpoints.ListRuns(
            http, observatory, new FakeAdminQuery(), authorizer, NullLoggerFactory.Instance, scope: OtherScope);

        observatory.ListScopes.Should().ContainSingle().Which.Should().Be(OtherScope);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ListRuns_AllScopes_Admin_UsesAdminQuery()
    {
        var adminQuery = new FakeAdminQuery();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        await WorkflowRunObservatoryEndpoints.ListRuns(
            http, new FakeObservatory(), adminQuery, authorizer, NullLoggerFactory.Instance,
            scope: WorkflowRunObservatoryEndpoints.AllScopesToken);

        adminQuery.ListAllCalls.Should().Be(1);
    }

    [Fact]
    public async Task ListRuns_AllScopes_NonAdmin_Denied_NoAdminQuery()
    {
        var adminQuery = new FakeAdminQuery();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.ListRuns(
            http, new FakeObservatory(), adminQuery, authorizer, NullLoggerFactory.Instance,
            scope: WorkflowRunObservatoryEndpoints.AllScopesToken);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(403);
        adminQuery.ListAllCalls.Should().Be(0);
    }

    [Fact]
    public async Task GetRun_CrossScope_NonAdmin_Denied_NeverReadsRun()
    {
        var observatory = new FakeObservatory();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.GetRun(
            http, "run-1", observatory, authorizer, NullLoggerFactory.Instance, scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(403);
        observatory.GetScopes.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRun_CrossScope_Admin_ReadsTargetScope()
    {
        var observatory = new FakeObservatory { Detail = new ObservatoryRunDetail() };
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        await WorkflowRunObservatoryEndpoints.GetRun(
            http, "run-1", observatory, authorizer, NullLoggerFactory.Instance, scope: OtherScope);

        observatory.GetScopes.Should().ContainSingle().Which.Should().Be(OtherScope);
    }

    [Fact]
    public async Task GetAdminRun_NonAdmin_Denied_AndNeverQueries()
    {
        var adminQuery = new FakeAdminQuery { Detail = new ObservatoryRunDetail() };
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.GetAdminRun(
            http, "run-1", adminQuery, authorizer, NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(403);
        adminQuery.GetRunCalls.Should().Be(0);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetAdminRun_MissingBearer_Returns401_AndNeverQueries()
    {
        var adminQuery = new FakeAdminQuery { Detail = new ObservatoryRunDetail() };
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: null);

        var result = await WorkflowRunObservatoryEndpoints.GetAdminRun(
            http, "run-1", adminQuery, authorizer, NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(401);
        adminQuery.GetRunCalls.Should().Be(0);
        authorizer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetAdminRun_Admin_ReadsRunByIdAcrossScopes()
    {
        var adminQuery = new FakeAdminQuery { Detail = new ObservatoryRunDetail() };
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.GetAdminRun(
            http, "run-1", adminQuery, authorizer, NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(200);
        adminQuery.GetRunCalls.Should().Be(1);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetAdminRun_AdminMissingRun_Returns404()
    {
        var adminQuery = new FakeAdminQuery();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.GetAdminRun(
            http, "run-missing", adminQuery, authorizer, NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(404);
        adminQuery.GetRunCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetAdminRunGraph_NonAdmin_Denied_AndNeverQueries()
    {
        var adminQuery = new FakeAdminQuery { Graph = new ObservatoryRunGraph() };
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.GetAdminRunGraph(
            http, "run-1", adminQuery, authorizer, NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(403);
        adminQuery.GetRunGraphCalls.Should().Be(0);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetAdminRunGraph_Admin_ReadsGraphByIdAcrossScopes()
    {
        var adminQuery = new FakeAdminQuery { Graph = new ObservatoryRunGraph() };
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.GetAdminRunGraph(
            http, "run-1", adminQuery, authorizer, NullLoggerFactory.Instance);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(200);
        adminQuery.GetRunGraphCalls.Should().Be(1);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetMe_ReflectsAuthorizerElevation()
    {
        var authorizer = new FakeAuthorizer(elevated: true, role: "operator", email: "ean@x.io");
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.GetMe(http, authorizer);
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(200);
        body.Should().Contain("\"isAdmin\":true").And.Contain("operator").And.Contain(OwnScope);
    }

    [Fact]
    public async Task ResolveScope_NonAdmin_Denied_NeverSearches()
    {
        var directory = new FakeDirectory();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.ResolveScope(
            http, authorizer, directory, NullLoggerFactory.Instance, email: "x@y.io");
        var status = await ExecuteAsync(result, http);

        status.Should().Be(403);
        directory.SearchCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveScope_Admin_ReturnsCandidates()
    {
        var directory = new FakeDirectory
        {
            Matches = [new PlatformUserMatch("scope-bob", "bob@x.io", "user")],
        };
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.ResolveScope(
            http, authorizer, directory, NullLoggerFactory.Instance, email: "bob@x.io");
        var (status, body) = await ExecuteWithBodyAsync(result, http);

        status.Should().Be(200);
        directory.SearchCount.Should().Be(1);
        body.Should().Contain("scope-bob").And.Contain("bob@x.io");
    }

    [Fact]
    public async Task WorkflowObservatoryRoute_ShouldAppendEndpointAuditRecords()
    {
        var appender = new RecordingAuditTrailAppender();
        var observatory = new FakeObservatory
        {
            Detail = new ObservatoryRunDetail(),
        };
        await using var app = await CreateRouteAuditAppAsync(
            appender,
            observatory,
            new FakeAdminQuery(),
            new FakeAuthorizer(elevated: true),
            new FakeDirectory());
        using var client = CreateClient(app);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/workflow/observatory/runs/run-a?scope={OtherScope}&access_token={RawToken}&email=alice@example.com");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", RawToken);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be("workflow.observatory.get-run.attempted");
        appender.Records[0].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records[0].ResultSummary.Should().BeEmpty();
        appender.Records[1].OperationName.Should().Be("workflow.observatory.get-run");
        appender.Records[1].Outcome.Should().Be(AuditOutcome.Accepted);
        appender.Records.Should().OnlyContain(record =>
            record.Target.Kind == "workflow-run" &&
            record.Target.Id == $"{OtherScope}/run-a" &&
            record.RequestSummary == "GET /api/workflow/observatory/runs/{runId} scope=scope-bob runId=run-a" &&
            record.CapturePlane == AuditCapturePlane.BoundaryEndpoint);
        appender.Records.SelectMany(RecordStrings).Should().NotContain(value =>
            value.Contains(RawToken, StringComparison.Ordinal) ||
            value.Contains("alice@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkflowObservatoryAdminRunRoute_ShouldAppendEndpointAuditRecords()
    {
        var appender = new RecordingAuditTrailAppender();
        var adminQuery = new FakeAdminQuery
        {
            Detail = new ObservatoryRunDetail(),
        };
        await using var app = await CreateRouteAuditAppAsync(
            appender,
            new FakeObservatory(),
            adminQuery,
            new FakeAuthorizer(elevated: true),
            new FakeDirectory());
        using var client = CreateClient(app);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/workflow/observatory/admin/runs/run-a?access_token={RawToken}&email=alice@example.com");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", RawToken);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        appender.Records.Should().HaveCount(2);
        appender.Records[0].OperationName.Should().Be("workflow.observatory.admin.get-run.attempted");
        appender.Records[1].OperationName.Should().Be("workflow.observatory.admin.get-run");
        appender.Records.Should().OnlyContain(record =>
            record.Target.Kind == "workflow-run" &&
            record.Target.Id == "run-a" &&
            record.RequestSummary == "GET /api/workflow/observatory/admin/runs/{runId} runId=run-a" &&
            record.CapturePlane == AuditCapturePlane.BoundaryEndpoint);
        appender.Records.SelectMany(RecordStrings).Should().NotContain(value =>
            value.Contains(RawToken, StringComparison.Ordinal) ||
            value.Contains("alice@example.com", StringComparison.Ordinal));
    }

    // Harness.

    private static async Task<WebApplication> CreateRouteAuditAppAsync(
        RecordingAuditTrailAppender appender,
        IWorkflowRunObservatoryQueryService observatory,
        IWorkflowRunAdminQueryService adminQuery,
        IPlatformAdminAuthorizer authorizer,
        IPlatformUserDirectory directory)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, RouteAuditAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();
        builder.Services.AddOptions();
        builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        builder.Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        builder.Services.AddSingleton<IAuditTrailAppender>(appender);
        builder.Services.AddSingleton<IAuditActorIdentityHasher>(new StableAuditActorIdentityHasher());
        builder.Services.AddSingleton(observatory);
        builder.Services.AddSingleton(adminQuery);
        builder.Services.AddSingleton(authorizer);
        builder.Services.AddSingleton(directory);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<EndpointAuditCaptureMiddleware>();
        app.UseAuthorization();
        app.MapWorkflowRunObservatory();
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        return new HttpClient
        {
            BaseAddress = new Uri(address),
        };
    }

    private static DefaultHttpContext BuildHttpContext(string scopeClaim, string? bearer)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()) // empty => auth enabled by default
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();

        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim("scope_id", scopeClaim));

        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(identity),
        };
        http.Response.Body = new MemoryStream();
        if (bearer is not null)
            http.Request.Headers.Authorization = $"Bearer {bearer}";
        return http;
    }

    private static async Task<int> ExecuteAsync(IResult result, HttpContext http)
    {
        await result.ExecuteAsync(http);
        return http.Response.StatusCode;
    }

    private static async Task<(int Status, string Body)> ExecuteWithBodyAsync(IResult result, HttpContext http)
    {
        await result.ExecuteAsync(http);
        http.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(http.Response.Body, Encoding.UTF8, leaveOpen: true);
        return (http.Response.StatusCode, await reader.ReadToEndAsync());
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

    private sealed class FakeAuthorizer(bool elevated, string role = "admin", string email = "a@x.io", string userId = "u1")
        : IPlatformAdminAuthorizer
    {
        public int Calls { get; private set; }

        public Task<PlatformCaller> ResolveCallerAsync(string bearerToken, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(elevated
                ? new PlatformCaller(true, role, email, userId)
                : PlatformCaller.NotElevated);
        }
    }

    private sealed class FakeObservatory : IWorkflowRunObservatoryQueryService
    {
        public List<string> ListScopes { get; } = [];
        public List<string> GetScopes { get; } = [];
        public ObservatoryRunDetail? Detail { get; init; }

        public Task<IReadOnlyList<ObservatoryRunSummary>> ListRunsForScopeAsync(string scopeId, ObservatoryRunListFilter filter, CancellationToken ct = default)
        {
            ListScopes.Add(scopeId);
            return Task.FromResult<IReadOnlyList<ObservatoryRunSummary>>([]);
        }

        public Task<ObservatoryRunDetail?> GetRunForScopeAsync(string scopeId, string runId, CancellationToken ct = default)
        {
            GetScopes.Add(scopeId);
            return Task.FromResult(Detail);
        }

        public Task<ObservatoryRunGraph?> GetRunGraphForScopeAsync(string scopeId, string runId, CancellationToken ct = default)
        {
            GetScopes.Add(scopeId);
            return Task.FromResult<ObservatoryRunGraph?>(new ObservatoryRunGraph());
        }
    }

    private sealed class FakeAdminQuery : IWorkflowRunAdminQueryService
    {
        public int ListAllCalls { get; private set; }
        public int GetRunCalls { get; private set; }
        public int GetRunGraphCalls { get; private set; }
        public ObservatoryRunDetail? Detail { get; init; }
        public ObservatoryRunGraph? Graph { get; init; }

        public Task<IReadOnlyList<ObservatoryRunSummary>> ListAllRunsAsync(ObservatoryRunListFilter filter, CancellationToken ct = default)
        {
            ListAllCalls++;
            return Task.FromResult<IReadOnlyList<ObservatoryRunSummary>>([]);
        }

        public Task<ObservatoryRunDetail?> GetRunAsync(string runId, CancellationToken ct = default)
        {
            GetRunCalls++;
            return Task.FromResult(Detail);
        }

        public Task<ObservatoryRunGraph?> GetRunGraphAsync(string runId, CancellationToken ct = default)
        {
            GetRunGraphCalls++;
            return Task.FromResult(Graph);
        }
    }

    private sealed class FakeDirectory : IPlatformUserDirectory
    {
        public int SearchCount { get; private set; }
        public IReadOnlyList<PlatformUserMatch> Matches { get; init; } = [];

        public Task<IReadOnlyList<PlatformUserMatch>> SearchByEmailAsync(string bearerToken, string email, CancellationToken ct = default)
        {
            SearchCount++;
            return Task.FromResult(Matches);
        }
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(
                record.AuditId,
                record.AuditActorId,
                record.OccurredAt.ToDateTimeOffset()));
        }
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new($"hashed:{canonicalActorKey}", "kid-test");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            auditActorId == $"hashed:{canonicalActorKey}" &&
            identityKeyId == "kid-test";
    }

    private sealed class RouteAuditAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorization) ||
                !authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
            [
                new Claim("sub", "user-123"),
                new Claim("scope_id", OwnScope),
            ], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
