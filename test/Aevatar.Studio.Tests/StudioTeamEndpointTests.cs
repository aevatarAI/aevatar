using System.Reflection;
using System.Security.Claims;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class StudioTeamEndpointTests
{
    private const string ScopeId = "scope-1";
    private const string TeamId = "t-1";

    [Fact]
    public async Task HandleCreateAsync_ShouldReturn201_WhenSuccessful()
    {
        var service = new InMemoryTeamService(NewSummary());
        var result = await InvokeTeamHandle(
            "HandleCreateAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new CreateStudioTeamRequest(DisplayName: "Alpha"),
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldReturn400_WhenValidationFails()
    {
        var service = new ThrowingTeamService(new InvalidOperationException("displayName is required"));
        var result = await InvokeTeamHandle(
            "HandleCreateAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new CreateStudioTeamRequest(DisplayName: ""),
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleListAsync_ShouldReturn200()
    {
        var service = new InMemoryTeamService(NewSummary());
        var result = await InvokeTeamHandle(
            "HandleListAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            service,
            (int?)null,
            (string?)null,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandleGetAsync_ShouldReturn200_WhenTeamExists()
    {
        var service = new InMemoryTeamService(NewSummary());
        var result = await InvokeTeamHandle(
            "HandleGetAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandleGetAsync_ShouldReturn404_WhenTeamMissing()
    {
        var service = new ThrowingTeamService(new StudioTeamNotFoundException(ScopeId, "missing"));
        var result = await InvokeTeamHandle(
            "HandleGetAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "missing",
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldReturn200_WhenUpdateSucceeds()
    {
        var service = new InMemoryTeamService(NewSummary());
        var body = new StudioTeamEndpoints.StudioTeamPatchBody
        {
            DisplayName = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("\"Beta\""),
        };
        var result = await InvokeTeamHandle(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            body,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldReturn400_WhenDisplayNameIsNull()
    {
        var service = new InMemoryTeamService(NewSummary());
        var body = new StudioTeamEndpoints.StudioTeamPatchBody
        {
            DisplayName = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("null"),
        };
        var result = await InvokeTeamHandle(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            body,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldReturn400_WhenDisplayNameIsEmpty()
    {
        var service = new InMemoryTeamService(NewSummary());
        var body = new StudioTeamEndpoints.StudioTeamPatchBody
        {
            DisplayName = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("\"\""),
        };
        var result = await InvokeTeamHandle(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            body,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldReturn404_WhenTeamNotFound()
    {
        var service = new ThrowingTeamService(new StudioTeamNotFoundException(ScopeId, TeamId));
        var body = new StudioTeamEndpoints.StudioTeamPatchBody
        {
            DisplayName = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("\"Beta\""),
        };
        var result = await InvokeTeamHandle(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            body,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldReturn400_WhenDescriptionIsNumber()
    {
        var service = new InMemoryTeamService(NewSummary());
        var body = new StudioTeamEndpoints.StudioTeamPatchBody
        {
            Description = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("42"),
        };
        var result = await InvokeTeamHandle(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            body,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldAllowNullDescription()
    {
        var service = new InMemoryTeamService(NewSummary());
        var body = new StudioTeamEndpoints.StudioTeamPatchBody
        {
            Description = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("null"),
        };
        var result = await InvokeTeamHandle(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            body,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldAllowStringDescription()
    {
        var service = new InMemoryTeamService(NewSummary());
        var body = new StudioTeamEndpoints.StudioTeamPatchBody
        {
            Description = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("\"new desc\""),
        };
        var result = await InvokeTeamHandle(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            body,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandleArchiveAsync_ShouldReturn200_WhenSuccessful()
    {
        var service = new InMemoryTeamService(NewSummary());
        var result = await InvokeTeamHandle(
            "HandleArchiveAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandleArchiveAsync_ShouldReturn404_WhenTeamNotFound()
    {
        var service = new ThrowingTeamService(new StudioTeamNotFoundException(ScopeId, TeamId));
        var result = await InvokeTeamHandle(
            "HandleArchiveAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleListMembersAsync_ShouldReturn200_WithFilteredMembers()
    {
        var teamService = new InMemoryTeamService(NewSummary());
        var memberService = new InMemoryMemberService(TeamId);
        var result = await InvokeTeamHandle(
            "HandleListMembersAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            teamService,
            memberService,
            (int?)null,
            (string?)null,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandleListMembersAsync_ShouldReturn404_WhenTeamNotFound()
    {
        var teamService = new ThrowingTeamService(new StudioTeamNotFoundException(ScopeId, "missing"));
        var memberService = new InMemoryMemberService(null);
        var result = await InvokeTeamHandle(
            "HandleListMembersAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "missing",
            teamService,
            memberService,
            (int?)null,
            (string?)null,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status404NotFound);
    }

    private static StudioTeamSummaryResponse NewSummary() =>
        new(
            TeamId: TeamId,
            ScopeId: ScopeId,
            DisplayName: "Alpha",
            Description: "desc",
            LifecycleStage: TeamLifecycleStageNames.Active,
            MemberCount: 0,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow);

    private static HttpContext CreateAuthenticatedContext(string scopeId)
    {
        var identity = new ClaimsIdentity([new Claim("scope_id", scopeId)], "test");
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

    private static async Task<IResult> InvokeTeamHandle(string methodName, params object?[] args)
    {
        var method = typeof(StudioTeamEndpoints).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        var task = (Task<IResult>)method.Invoke(null, args)!;
        return await task;
    }

    private static int? GetStatusCode(IResult result)
    {
        return result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
    }

    private sealed class InMemoryTeamService : IStudioTeamService
    {
        private readonly StudioTeamSummaryResponse _summary;

        public InMemoryTeamService(StudioTeamSummaryResponse summary) => _summary = summary;

        public Task<StudioTeamSummaryResponse> CreateAsync(
            string scopeId, CreateStudioTeamRequest request, CancellationToken ct = default) =>
            Task.FromResult(_summary);

        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, [_summary]));

        public Task<StudioTeamSummaryResponse> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromResult(_summary);

        public Task<StudioTeamSummaryResponse> UpdateAsync(
            string scopeId, string teamId, UpdateStudioTeamRequest request, CancellationToken ct = default) =>
            Task.FromResult(_summary);

        public Task<StudioTeamSummaryResponse> ArchiveAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromResult(_summary);
    }

    private sealed class ThrowingTeamService : IStudioTeamService
    {
        private readonly Exception _ex;
        public ThrowingTeamService(Exception ex) => _ex = ex;

        public Task<StudioTeamSummaryResponse> CreateAsync(
            string scopeId, CreateStudioTeamRequest request, CancellationToken ct = default) => throw _ex;
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) => throw _ex;
        public Task<StudioTeamSummaryResponse> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) => throw _ex;
        public Task<StudioTeamSummaryResponse> UpdateAsync(
            string scopeId, string teamId, UpdateStudioTeamRequest request, CancellationToken ct = default) => throw _ex;
        public Task<StudioTeamSummaryResponse> ArchiveAsync(
            string scopeId, string teamId, CancellationToken ct = default) => throw _ex;
    }

    private sealed class InMemoryMemberService : IStudioMemberService
    {
        private readonly string? _teamId;
        public InMemoryMemberService(string? teamId) => _teamId = teamId;

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId, StudioMemberRosterPageRequest? page = null, CancellationToken ct = default)
        {
            var members = new List<StudioMemberSummaryResponse>
            {
                new(MemberId: "m-1", ScopeId: scopeId, DisplayName: "M1", Description: "",
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    LifecycleStage: MemberLifecycleStageNames.Created,
                    PublishedServiceId: "member-m-1", LastBoundRevisionId: null,
                    CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow) { TeamId = _teamId },
                new(MemberId: "m-2", ScopeId: scopeId, DisplayName: "M2", Description: "",
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    LifecycleStage: MemberLifecycleStageNames.Created,
                    PublishedServiceId: "member-m-2", LastBoundRevisionId: null,
                    CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow) { TeamId = "other-team" },
            };
            return Task.FromResult(new StudioMemberRosterResponse(scopeId, members));
        }

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<StudioMemberBindingResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<StudioMemberBindingContractResponse?> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId, string memberId, string endpointId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<StudioMemberDetailResponse> UpdateAsync(
            string scopeId, string memberId, UpdateStudioMemberRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
