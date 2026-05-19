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
    public async Task HandlePatchAsync_ShouldReturn202Accepted_WhenUpdateSucceeds()
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

        GetStatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        GetLocation(result).Should().Be($"/api/scopes/{ScopeId}/teams/{TeamId}");

        var accepted = GetValue<StudioTeamCommandAcceptedResponse>(result);
        accepted.ScopeId.Should().Be(ScopeId);
        accepted.TeamId.Should().Be(TeamId);
        accepted.CommandId.Should().Be("cmd-update");
        accepted.AckStage.Should().Be(StudioTeamCommandAckStageNames.Accepted);
        accepted.AcceptedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        AssertDoesNotContainPostStateFields(SerializeCamelCase(accepted));
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

        GetStatusCode(result).Should().Be(StatusCodes.Status202Accepted);
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

        GetStatusCode(result).Should().Be(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldReturnAcceptedReceiptWithNullCommandId_WhenNoop()
    {
        var service = new InMemoryTeamService(NewSummary());
        var result = await InvokeTeamHandle(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            new StudioTeamEndpoints.StudioTeamPatchBody(),
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        var accepted = GetValue<StudioTeamCommandAcceptedResponse>(result);
        accepted.CommandId.Should().BeNull();
        accepted.AckStage.Should().Be(StudioTeamCommandAckStageNames.Accepted);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldUseEscapedLocationFromAcceptedReceipt()
    {
        var service = new InMemoryTeamService(
            NewSummary(),
            updateReceipt: NewAcceptedReceipt("scope with/slash", "team with/slash", "cmd-update"));
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

        GetStatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        GetLocation(result).Should().Be("/api/scopes/scope%20with%2Fslash/teams/team%20with%2Fslash");
    }

    [Fact]
    public async Task HandleArchiveAsync_ShouldReturn202Accepted_WhenSuccessful()
    {
        var service = new InMemoryTeamService(NewSummary());
        var result = await InvokeTeamHandle(
            "HandleArchiveAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            TeamId,
            service,
            CancellationToken.None);

        GetStatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        GetLocation(result).Should().Be($"/api/scopes/{ScopeId}/teams/{TeamId}");

        var accepted = GetValue<StudioTeamCommandAcceptedResponse>(result);
        accepted.ScopeId.Should().Be(ScopeId);
        accepted.TeamId.Should().Be(TeamId);
        accepted.CommandId.Should().Be("cmd-archive");
        accepted.AckStage.Should().Be(StudioTeamCommandAckStageNames.Accepted);
        AssertDoesNotContainPostStateFields(SerializeCamelCase(accepted));
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

    private static string? GetLocation(IResult result) =>
        result.GetType().GetProperty("Location")?.GetValue(result) as string;

    private static T GetValue<T>(IResult result) where T : class =>
        result.GetType().GetProperty("Value")?.GetValue(result) as T
        ?? throw new InvalidOperationException($"Result does not carry {typeof(T).Name}.");

    private static string SerializeCamelCase<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(
            value,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });

    private static void AssertDoesNotContainPostStateFields(string json)
    {
        foreach (var field in new[]
        {
            "displayName",
            "description",
            "lifecycleStage",
            "memberCount",
            "createdAt",
            "updatedAt",
        })
        {
            json.Should().NotContain(field);
        }
    }

    private sealed class InMemoryTeamService : IStudioTeamService
    {
        private readonly StudioTeamSummaryResponse _summary;
        private readonly StudioTeamCommandAcceptedResponse _updateReceipt;
        private readonly StudioTeamCommandAcceptedResponse _archiveReceipt;

        public InMemoryTeamService(
            StudioTeamSummaryResponse summary,
            StudioTeamCommandAcceptedResponse? updateReceipt = null,
            StudioTeamCommandAcceptedResponse? archiveReceipt = null)
        {
            _summary = summary;
            _updateReceipt = updateReceipt ?? NewAcceptedReceipt(summary.ScopeId, summary.TeamId, "cmd-update");
            _archiveReceipt = archiveReceipt ?? NewAcceptedReceipt(summary.ScopeId, summary.TeamId, "cmd-archive");
        }

        public Task<StudioTeamSummaryResponse> CreateAsync(
            string scopeId, CreateStudioTeamRequest request, CancellationToken ct = default) =>
            Task.FromResult(_summary);

        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, [_summary]));

        public Task<StudioTeamSummaryResponse> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromResult(_summary);

        public Task<StudioTeamCommandAcceptedResponse> UpdateAsync(
            string scopeId, string teamId, UpdateStudioTeamRequest request, CancellationToken ct = default)
        {
            if (!request.DisplayName.HasValue && !request.Description.HasValue)
                return Task.FromResult(_updateReceipt with { CommandId = null });
            return Task.FromResult(_updateReceipt);
        }

        public Task<StudioTeamCommandAcceptedResponse> ArchiveAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromResult(_archiveReceipt);
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
        public Task<StudioTeamCommandAcceptedResponse> UpdateAsync(
            string scopeId, string teamId, UpdateStudioTeamRequest request, CancellationToken ct = default) => throw _ex;
        public Task<StudioTeamCommandAcceptedResponse> ArchiveAsync(
            string scopeId, string teamId, CancellationToken ct = default) => throw _ex;
    }

    private static StudioTeamCommandAcceptedResponse NewAcceptedReceipt(
        string scopeId,
        string teamId,
        string? commandId) =>
        new(
            ScopeId: scopeId,
            TeamId: teamId,
            CommandId: commandId,
            AckStage: StudioTeamCommandAckStageNames.Accepted,
            AcceptedAtUtc: DateTimeOffset.UtcNow);

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
        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId, string memberId, string bindingRunId, CancellationToken ct = default) =>
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
