using System.Security.Claims;
using System.Text.Json;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatStateEndpointTests
{
    private const string StateRoute =
        "/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}/state";

    [Fact]
    public async Task GetState_ShouldReturnCurrentSnapshotFromTypedQueryPort()
    {
        var queryPort = new RecordingQueryPort
        {
            Result = NyxIdChatConversationStateQueryResult.Current(new NyxIdChatConversationStateSnapshot(
                "conversation-alpha",
                "scope-alpha",
                8,
                34,
                DateTimeOffset.Parse("2026-07-25T06:20:00Z"),
                new NyxIdChatConversationTurnSnapshot(
                    "turn-alpha", "task-alpha", "active", null, null, null, null),
                null,
                [],
                null,
                new NyxIdChatPendingApprovalSnapshot(
                    ApprovalRequestId: "approval-alpha",
                    TurnId: "turn-alpha",
                    TaskId: "task-alpha",
                    StepId: "step-alpha",
                    ToolName: "service.connect",
                    ExpiresAt: null,
                    AskedAt: DateTimeOffset.Parse("2026-07-25T06:19:00Z"),
                    Action: "connect",
                    Target: "service-alpha",
                    ActorLabel: "Aevatar Assistant",
                    Reversibility: "reversible",
                    GrantBoundary: "nyxid_step_up",
                    NyxIdRequestId: "nyx-request-alpha"),
                [],
                null,
                null,
                null)),
        };

        var response = await ExecuteAsync(
            queryPort,
            "?afterStateVersion=7&turnId=turn-alpha");

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var query = queryPort.Queries.Should().ContainSingle().Subject;
        query.ScopeId.Should().Be("scope-alpha");
        query.ActorId.Should().Be("conversation-alpha");
        query.AfterStateVersion.Should().Be(7);
        query.TurnId.Should().Be("turn-alpha");
        using var json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("status").GetString().Should().Be("current");
        json.RootElement.GetProperty("stateVersion").GetInt64().Should().Be(8);
        json.RootElement.GetProperty("turnId").GetString().Should().Be("turn-alpha");
        json.RootElement.GetProperty("snapshot").GetProperty("actorId").GetString()
            .Should().Be("conversation-alpha");
        var pendingApproval = json.RootElement
            .GetProperty("snapshot")
            .GetProperty("pendingApproval");
        pendingApproval.GetProperty("nyxidRequestId").GetString()
            .Should().Be("nyx-request-alpha");
        pendingApproval.TryGetProperty("nyxIdRequestId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetState_ShouldReturnReloadRequiredForInvalidNumericCursorWithoutQuerying()
    {
        var queryPort = new RecordingQueryPort();

        var response = await ExecuteAsync(queryPort, "?afterStateVersion=not-a-version");

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        queryPort.Queries.Should().BeEmpty();
        using var json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("status").GetString().Should().Be("reload_required");
        json.RootElement.GetProperty("reasonCode").GetString()
            .Should().Be("invalid_state_version");
    }

    [Fact]
    public async Task GetState_ShouldReturnNotFoundFromReadModelQuery()
    {
        var queryPort = new RecordingQueryPort
        {
            Result = NyxIdChatConversationStateQueryResult.NotFound(),
        };

        var response = await ExecuteAsync(queryPort, string.Empty);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        using var json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("status").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task GetState_ShouldNotReadConversationStateWhenRegistryDoesNotOwnActor()
    {
        var queryPort = new RecordingQueryPort();
        var registry = new RecordingRegistryQueryPort
        {
            Snapshot = new GAgentActorRegistrySnapshot(
                "scope-alpha",
                [new GAgentActorGroup(NyxIdChatServiceDefaults.GAgentKind, ["conversation-other"])],
                3,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
        };

        var response = await ExecuteAsync(
            queryPort,
            string.Empty,
            registryQueryPort: registry);

        response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        queryPort.Queries.Should().BeEmpty();
        registry.ScopeIds.Should().ContainSingle("scope-alpha");
    }

    [Fact]
    public async Task GetState_ShouldRejectAuthenticatedScopeMismatchBeforeQuery()
    {
        var queryPort = new RecordingQueryPort();

        var response = await ExecuteAsync(
            queryPort,
            string.Empty,
            authenticatedScopeId: "scope-other");

        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        queryPort.Queries.Should().BeEmpty();
    }

    [Fact]
    public void StateEndpointSource_ShouldStayReadModelOnly()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.State.cs"));

        source.Should().Contain("INyxIdChatConversationStateQueryPort");
        source.Should().NotContain("IActorRuntime");
        source.Should().NotContain("IEventStore");
        source.Should().NotContain("INyxIdChatSessionProjectionPort");
        source.Should().NotContain("ActivateAsync");
        source.Should().NotContain("PrimeAsync");
        source.Should().NotContain("EnsureAndAttachLeaseAsync");
    }

    private static async Task<(int StatusCode, string Body)> ExecuteAsync(
        INyxIdChatConversationStateQueryPort queryPort,
        string queryString,
        string? authenticatedScopeId = null,
        IGAgentActorRegistryQueryPort? registryQueryPort = null)
    {
        registryQueryPort ??= RecordingRegistryQueryPort.OwningConversation();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = authenticatedScopeId is null
                        ? "false"
                        : "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment
            {
                EnvironmentName = authenticatedScopeId is null
                    ? Environments.Development
                    : Environments.Production,
            })
            .AddSingleton(registryQueryPort)
            .AddSingleton(queryPort)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        if (authenticatedScopeId is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("scope_id", authenticatedScopeId)],
                authenticationType: "test"));
        }

        context.Request.Method = HttpMethods.Get;
        context.Request.RouteValues = new RouteValueDictionary
        {
            ["scopeId"] = "scope-alpha",
            ["actorId"] = "conversation-alpha",
        };
        context.Request.QueryString = new QueryString(queryString);
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await BuildRouteEndpoint().RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (
            context.Response.StatusCode,
            await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private static RouteEndpoint BuildRouteEndpoint()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        var app = builder.Build();
        app.MapNyxIdChatEndpoints();
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => string.Equals(
                endpoint.RoutePattern.RawText,
                StateRoute,
                StringComparison.Ordinal));
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root could not be resolved.");
    }

    private sealed class RecordingQueryPort : INyxIdChatConversationStateQueryPort
    {
        public NyxIdChatConversationStateQueryResult Result { get; init; } =
            NyxIdChatConversationStateQueryResult.ReloadRequired(
                0,
                null,
                "unconfigured_test_result");
        public List<NyxIdChatConversationStateQuery> Queries { get; } = [];

        public Task<NyxIdChatConversationStateQueryResult> GetAsync(
            NyxIdChatConversationStateQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>
            GetAttentionSummariesAsync(
                string scopeId,
                IReadOnlyCollection<string> actorIds,
                CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, NyxIdChatConversationAttentionSummary>>(
                new Dictionary<string, NyxIdChatConversationAttentionSummary>());
    }

    private sealed class RecordingRegistryQueryPort : IGAgentActorRegistryQueryPort
    {
        public GAgentActorRegistrySnapshot Snapshot { get; init; } =
            new(
                "scope-alpha",
                [],
                0,
                DateTimeOffset.MinValue,
                DateTimeOffset.MinValue);
        public List<string> ScopeIds { get; } = [];

        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScopeIds.Add(scopeId);
            return Task.FromResult(Snapshot);
        }

        public static RecordingRegistryQueryPort OwningConversation() => new()
        {
            Snapshot = new GAgentActorRegistrySnapshot(
                "scope-alpha",
                [
                    new GAgentActorGroup(
                        NyxIdChatServiceDefaults.GAgentKind,
                        ["conversation-alpha"]),
                ],
                4,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
        };
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.AI.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
