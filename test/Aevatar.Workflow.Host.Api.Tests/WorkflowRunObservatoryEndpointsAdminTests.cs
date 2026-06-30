using System.Security.Claims;
using System.Text;
using Aevatar.Authentication.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Host.Api.Tests;

// 06-20-observatory-admin-cross-scope (G2): the endpoint auth matrix is the security crux — a non-admin must
// never reach a cross-scope query, and admin status comes only from the server-side authorizer.
public sealed class WorkflowRunObservatoryEndpointsAdminTests
{
    private const string OwnScope = "scope-alice";
    private const string OtherScope = "scope-bob";

    [Fact]
    public async Task ListRuns_NoScope_UsesOwnScope_AndNeverCallsAuthorizerOrOverview()
    {
        var observatory = new FakeObservatory();
        var overview = new FakeOverview();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.ListRuns(
            http, observatory, overview, authorizer, NullLoggerFactory.Instance, scope: null);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(200);
        observatory.ListScopes.Should().ContainSingle().Which.Should().Be(OwnScope);
        overview.ListAllCalls.Should().Be(0);
        authorizer.Calls.Should().Be(0); // own scope => no NyxID round-trip
    }

    [Fact]
    public async Task ListRuns_ScopeEqualsOwn_IsTreatedAsOwn_NoAuthorizer()
    {
        var observatory = new FakeObservatory();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        await WorkflowRunObservatoryEndpoints.ListRuns(
            http, observatory, new FakeOverview(), authorizer, NullLoggerFactory.Instance, scope: OwnScope);

        observatory.ListScopes.Should().ContainSingle().Which.Should().Be(OwnScope);
        authorizer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ListRuns_CrossScope_NonAdmin_Denied_AndNeverQueries()
    {
        var observatory = new FakeObservatory();
        var overview = new FakeOverview();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.ListRuns(
            http, observatory, overview, authorizer, NullLoggerFactory.Instance, scope: OtherScope);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(403);
        // The cross-scope query MUST NOT have been reached.
        observatory.ListScopes.Should().BeEmpty();
        overview.ListAllCalls.Should().Be(0);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ListRuns_CrossScope_MissingBearer_Returns401_BeforeAuthorizer()
    {
        var observatory = new FakeObservatory();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: null);

        var result = await WorkflowRunObservatoryEndpoints.ListRuns(
            http, observatory, new FakeOverview(), authorizer, NullLoggerFactory.Instance, scope: OtherScope);
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
            http, observatory, new FakeOverview(), authorizer, NullLoggerFactory.Instance, scope: OtherScope);

        observatory.ListScopes.Should().ContainSingle().Which.Should().Be(OtherScope);
        authorizer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ListRuns_AllScopes_Admin_UsesOverview()
    {
        var overview = new FakeOverview();
        var authorizer = new FakeAuthorizer(elevated: true);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        await WorkflowRunObservatoryEndpoints.ListRuns(
            http, new FakeObservatory(), overview, authorizer, NullLoggerFactory.Instance,
            scope: WorkflowRunObservatoryEndpoints.AllScopesToken);

        overview.ListAllCalls.Should().Be(1);
    }

    [Fact]
    public async Task ListRuns_AllScopes_NonAdmin_Denied_NoOverview()
    {
        var overview = new FakeOverview();
        var authorizer = new FakeAuthorizer(elevated: false);
        var http = BuildHttpContext(OwnScope, bearer: "tok");

        var result = await WorkflowRunObservatoryEndpoints.ListRuns(
            http, new FakeObservatory(), overview, authorizer, NullLoggerFactory.Instance,
            scope: WorkflowRunObservatoryEndpoints.AllScopesToken);
        var status = await ExecuteAsync(result, http);

        status.Should().Be(403);
        overview.ListAllCalls.Should().Be(0);
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

    // ── harness ──

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

    private sealed class FakeOverview : IWorkflowRunAdminOverviewQueryService
    {
        public int ListAllCalls { get; private set; }

        public Task<IReadOnlyList<ObservatoryRunSummary>> ListAllRunsAsync(ObservatoryRunListFilter filter, CancellationToken ct = default)
        {
            ListAllCalls++;
            return Task.FromResult<IReadOnlyList<ObservatoryRunSummary>>([]);
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
