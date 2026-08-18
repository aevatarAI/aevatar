using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AIWorkspace.Application;
using Aevatar.AIWorkspace.Application.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.Mainnet.Host.Api.AI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Application.Abstractions.Observatory;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetAIWorkspaceEndpointsTests
{
    [Fact]
    public void MapAIWorkspaceEndpoints_ShouldAuthorizeEveryApiRoute()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        app.MapAIWorkspaceEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint => new
            {
                Pattern = endpoint.RoutePattern.RawText,
                IsAuthorized = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null,
            })
            .ToArray();
        routes.Should().Contain(route => route.Pattern == "/ai" && !route.IsAuthorized);
        routes.Should().Contain(route => route.Pattern == "/ai/{**path}" && !route.IsAuthorized);
        routes.Should().Contain(route => route.Pattern == "/ai-assets/{**path}" && !route.IsAuthorized);
        routes.Should().Contain(route => route.Pattern == "/chat" && !route.IsAuthorized);
        routes.Should().Contain(route => route.Pattern == "/login" && !route.IsAuthorized);
        routes.Should().Contain(route => route.Pattern == "/auth/callback" && !route.IsAuthorized);
        routes.Should().Contain(route => route.Pattern == "/scopes" && !route.IsAuthorized);
        routes.Should().Contain(route => route.Pattern == "/scopes/{**path}" && !route.IsAuthorized);
        routes.Should().Contain(route => route.Pattern == "/settings" && !route.IsAuthorized);
        routes.Where(static route => route.Pattern?.StartsWith("/api/ai", StringComparison.Ordinal) == true)
            .Should()
            .OnlyContain(static route => route.IsAuthorized);
        routes.Select(static route => route.Pattern).Should().Contain([
            "/api/ai/context",
            "/api/ai/overview",
            "/api/ai/agents",
            "/api/ai/models",
            "/api/ai/activity",
            "/api/ai/activity/conversations",
            "/api/ai/activity/runs",
            "/api/ai/activity/runs/{runId}",
        ]);
    }

    [Fact]
    public async Task AIEntry_ShouldServeDeepLinkWithoutChangingTheOrigin()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);

        var response = await host.Client.GetAsync("/ai/activity/runs/run-alpha?tab=trace&view=full");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Headers.Location.Should().BeNull();
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        body.Should().Contain("data-ai-workspace-shell");
    }

    [Fact]
    public async Task TeamsEntry_ShouldServeDeepLinkWithoutChangingTheOrigin()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);

        var response = await host.Client.GetAsync("/scopes/scope-alpha/teams/team-alpha");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Headers.Location.Should().BeNull();
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        body.Should().Contain("data-ai-workspace-shell");
    }

    [Fact]
    public async Task AIEntry_WhenAssetsAreMissing_ShouldReturnStructuredUnavailable()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"aevatar-ai-workspace-missing-{Guid.NewGuid():N}");
        await using var host = await AIWorkspaceTestHost.StartAsync(missingPath);

        var response = await host.Client.GetAsync("/ai");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        body.Should().Contain("AI_CONSOLE_UNAVAILABLE");

        var assetResponse = await host.Client.GetAsync("/ai-assets/umi.01234567.js");
        assetResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await assetResponse.Content.ReadAsStringAsync()).Should().Contain("AI_CONSOLE_UNAVAILABLE");
    }

    [Fact]
    public async Task AIAssets_WithContentHash_ShouldUseImmutableCaching()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);

        var response = await host.Client.GetAsync("/ai-assets/app.01234567.js");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/javascript");
        response.Headers.CacheControl?.Public.Should().BeTrue();
        response.Headers.CacheControl?.MaxAge.Should().Be(TimeSpan.FromDays(365));
        body.Should().Be("globalThis.aiWorkspaceVersion = '01234567';");
    }

    [Fact]
    public async Task AIAssets_WithoutContentHash_ShouldRequireRevalidation()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);

        var response = await host.Client.GetAsync("/ai-assets/app.test.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoCache.Should().BeTrue();
        response.Headers.CacheControl?.Extensions
            .Should()
            .NotContain(extension => string.Equals(
                extension.Name,
                "immutable",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AIEntry_WithDottedOpaqueRunId_ShouldServeTheSpaDocument()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);

        var response = await host.Client.GetAsync("/ai/activity/runs/run-alpha.2026-08-18");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        body.Should().Contain("data-ai-workspace-shell");
    }

    [Fact]
    public async Task AIAssets_WhenFileDoesNotExist_ShouldReturnNotFound()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);

        var response = await host.Client.GetAsync("/ai-assets/missing.js");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/ai")]
    [InlineData("/ai/chat")]
    [InlineData("/login")]
    [InlineData("/auth/callback")]
    [InlineData("/scopes/scope-alpha/teams/team-alpha")]
    [InlineData("/settings")]
    [InlineData("/ai-assets/app.01234567.js")]
    public async Task AIWorkspaceWebRoutes_ShouldSupportHead(string path)
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);
        using var request = new HttpRequestMessage(HttpMethod.Head, path);

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAIWorkspace_WithEmptyStaticAssetsPath_ShouldFailOptionsValidation(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AIWorkspaceOptions.SectionName}:StaticAssetsPath"] = value,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddAIWorkspace(configuration);
        using var provider = services.BuildServiceProvider();

        var action = () => provider.GetRequiredService<IOptions<AIWorkspaceOptions>>().Value;

        action.Should().Throw<OptionsValidationException>()
            .WithMessage("*AIWorkspace:StaticAssetsPath*");
    }

    [Fact]
    public async Task Context_ShouldUseOnlyTheAuthenticatedScopeAndImplementedLinks()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/context");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("scopeId").GetString().Should().Be("scope-alpha");
        json.RootElement.GetProperty("consistency").GetString().Should().Be("independent_read_models");
        json.RootElement.GetProperty("pages").GetProperty("agents").GetString().Should().Be("/ai/agents");
        json.RootElement.GetProperty("pages").GetProperty("activity").GetString().Should().Be("/ai/activity");
        var apis = json.RootElement.GetProperty("apis");
        apis.GetProperty("overview").GetString().Should().Be("/api/ai/overview");
        apis.GetProperty("ownedAgentProfiles").GetString().Should().Be(
            "/api/scopes/scope-alpha/agent-profiles");
        apis.GetProperty("models").GetString().Should().Be("/api/ai/models");
        apis.GetProperty("activity").GetString().Should().Be("/api/ai/activity");
        apis.GetProperty("conversations").GetString().Should().Be("/api/ai/activity/conversations");
        apis.GetProperty("runs").GetString().Should().Be("/api/ai/activity/runs");
        apis.TryGetProperty("auditedActions", out _).Should().BeFalse();
        var features = json.RootElement.GetProperty("features");
        features.GetProperty("activity").GetProperty("availability").GetString().Should().Be("available");
        features.GetProperty("activity").GetProperty("page").GetString().Should().Be("/ai/activity");
        features.GetProperty("activity").GetProperty("api").GetString().Should().Be("/api/ai/activity");
    }

    [Fact]
    public async Task Context_WithAmbiguousScopeClaims_ShouldFailClosed()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha,scope-beta");

        var response = await host.Client.GetAsync("/api/ai/context");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AI_SCOPE_REQUIRED");
    }

    [Fact]
    public async Task Context_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);

        var response = await host.Client.GetAsync("/api/ai/context");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Context_WithAuthenticatedPrincipalWithoutScope_ShouldFailClosed()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync(null);
        host.Client.DefaultRequestHeaders.Add("X-Test-Authenticated", "true");

        var response = await host.Client.GetAsync("/api/ai/context");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("AI_SCOPE_REQUIRED");
    }

    [Fact]
    public async Task Overview_ShouldKeepEachReadModelSourceIndependent()
    {
        var catalog = Substitute.For<IAgentProfileCatalogQueryPort>();
        catalog.GetAsync(Arg.Any<AgentProfileOwner>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var owner = call.Arg<AgentProfileOwner>();
                return Task.FromResult<AgentProfileCatalogSnapshot?>(Snapshot(
                    owner,
                    owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.Scope ? 11 : 12,
                    DateTimeOffset.Parse("2026-08-18T01:00:00Z"),
                    Profile(
                        owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.Scope
                            ? "owned-alpha"
                            : "system-alpha",
                        "alpha",
                        2,
                        0x11)));
            });
        var chatHistory = Substitute.For<IChatHistoryQueryPort>();
        chatHistory.GetIndexAsync(Arg.Any<ChatHistoryIndexPageRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatHistoryIndexPage(
                [
                    new ConversationMeta(
                        "conversation-alpha",
                        "Alpha conversation",
                        "svc-alpha",
                        "assistant",
                        DateTimeOffset.Parse("2026-08-18T02:00:00Z"),
                        DateTimeOffset.Parse("2026-08-18T03:00:00Z"),
                        2,
                        StateVersion: 17),
                ],
                null)));
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.ListActivityRunsForScopeAsync(
                "scope-alpha",
                Arg.Any<WorkflowActivityRunFeedFilter>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkflowActivityRunFeedPage
            {
                Items =
                [
                    new WorkflowActivityRunFeedRow
                    {
                        RunId = "run-alpha",
                        WorkflowName = "Alpha workflow",
                        Status = "completed",
                        RunOrigin = "chat",
                        UpdatedAtUtc = DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
                        StateVersion = 19,
                    },
                ],
            }));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            catalog,
            chatHistory: chatHistory,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/overview?take=5");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("consistency").GetString()
            .Should().Be("independent_read_models");
        json.RootElement.GetProperty("agents").GetProperty("owned")
            .GetProperty("authorityStateVersion").GetInt64().Should().Be(11);
        json.RootElement.GetProperty("recentConversations").GetProperty("items")[0]
            .GetProperty("authorityStateVersion").GetInt64().Should().Be(17);
        json.RootElement.GetProperty("recentRuns").GetProperty("items")[0]
            .GetProperty("authorityStateVersion").GetInt64().Should().Be(19);
        await chatHistory.Received(1).GetIndexAsync(
            Arg.Is<ChatHistoryIndexPageRequest>(request =>
                request.ScopeId == "scope-alpha" && request.PageSize == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Agents_ShouldKeepOwnedAndSystemAuthoritiesIndependent()
    {
        var catalog = Substitute.For<IAgentProfileCatalogQueryPort>();
        catalog.GetAsync(Arg.Any<AgentProfileOwner>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var owner = call.Arg<AgentProfileOwner>();
                return Task.FromResult<AgentProfileCatalogSnapshot?>(owner.OwnerCase switch
                {
                    AgentProfileOwner.OwnerOneofCase.Scope => Snapshot(
                        owner,
                        11,
                        new DateTimeOffset(2026, 8, 18, 1, 0, 0, TimeSpan.Zero),
                        Profile("owned-alpha", "owned-alpha", 2, 0x11),
                        Profile("owned-draft", "owned-draft", 0, 0x00)),
                    AgentProfileOwner.OwnerOneofCase.System => Snapshot(
                        owner,
                        23,
                        new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero),
                        Profile("system-alpha", "system-alpha", 4, 0x22),
                        Profile("system-draft", "system-draft", 0, 0x00)),
                    _ => null,
                });
            });
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/agents?take=10");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var owned = json.RootElement.GetProperty("owned");
        owned.GetProperty("scopeId").GetString().Should().Be("scope-alpha");
        owned.GetProperty("authorityStateVersion").GetInt64().Should().Be(11);
        owned.GetProperty("items").GetArrayLength().Should().Be(2);
        owned.GetProperty("items")[0].GetProperty("published").GetBoolean().Should().BeTrue();
        owned.GetProperty("items")[1].GetProperty("published").GetBoolean().Should().BeFalse();
        owned.GetProperty("items")[0].GetProperty("publishedSnapshotSha256").GetString()
            .Should().Be(new string('1', 64));

        var templates = json.RootElement.GetProperty("systemTemplates");
        templates.GetProperty("scopeId").ValueKind.Should().Be(JsonValueKind.Null);
        templates.GetProperty("authorityStateVersion").GetInt64().Should().Be(23);
        templates.GetProperty("items").GetArrayLength().Should().Be(1);
        templates.GetProperty("items")[0].GetProperty("profileId").GetString().Should().Be("system-alpha");
    }

    [Fact]
    public async Task Agents_WhenSystemCatalogFails_ShouldKeepOwnedCatalogAvailable()
    {
        var catalog = Substitute.For<IAgentProfileCatalogQueryPort>();
        catalog.GetAsync(Arg.Any<AgentProfileOwner>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var owner = call.Arg<AgentProfileOwner>();
                if (owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.System)
                    throw new InvalidOperationException("system catalog unavailable");

                return Task.FromResult<AgentProfileCatalogSnapshot?>(Snapshot(
                    owner,
                    11,
                    DateTimeOffset.Parse("2026-08-18T01:00:00Z"),
                    Profile("owned-alpha", "owned-alpha", 2, 0x11)));
            });
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/agents");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetProperty("owned").GetProperty("availability").GetString()
            .Should().Be("available");
        var system = json.RootElement.GetProperty("systemTemplates");
        system.GetProperty("availability").GetString().Should().Be("unavailable");
        system.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("SYSTEM_AGENT_TEMPLATES_UNAVAILABLE");
    }

    [Fact]
    public async Task Agents_WhenCatalogIsNotMaterialized_ShouldKeepCountUnknown()
    {
        var catalog = Substitute.For<IAgentProfileCatalogQueryPort>();
        catalog.GetAsync(Arg.Any<AgentProfileOwner>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentProfileCatalogSnapshot?>(null));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var agentsResponse = await host.Client.GetAsync("/api/ai/agents");
        var agentsBody = await agentsResponse.Content.ReadAsStringAsync();

        agentsResponse.StatusCode.Should().Be(HttpStatusCode.OK, agentsBody);
        using (var agentsJson = JsonDocument.Parse(agentsBody))
        {
            var owned = agentsJson.RootElement.GetProperty("owned");
            owned.GetProperty("availability").GetString().Should().Be("not_materialized");
            owned.GetProperty("totalCount").ValueKind.Should().Be(JsonValueKind.Null);
            owned.GetProperty("authorityStateVersion").ValueKind.Should().Be(JsonValueKind.Null);
        }

        var overviewResponse = await host.Client.GetAsync("/api/ai/overview");
        var overviewBody = await overviewResponse.Content.ReadAsStringAsync();

        overviewResponse.StatusCode.Should().Be(HttpStatusCode.OK, overviewBody);
        using var overviewJson = JsonDocument.Parse(overviewBody);
        var overviewOwned = overviewJson.RootElement.GetProperty("agents").GetProperty("owned");
        overviewOwned.GetProperty("availability").GetString().Should().Be("not_materialized");
        overviewOwned.GetProperty("itemCount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Models_ShouldKeepPersonalAndScopeAuthoritiesIndependent()
    {
        var personal = Substitute.For<IUserLlmPreferenceService>();
        personal.GetSettingsAsync("personal-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserLlmSettingsView(
                null,
                "System default",
                UserLlmSelectionStatus.SystemDefault,
                LLMModelCatalogDiagnosticKind.Unspecified,
                UserLlmRemediationKind.None,
                [],
                [],
                UserLlmCatalogStatus.Ready,
                new UserLlmSettingsCapabilities(true, true, true, true),
                null)));
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        var source = new LLMModelCatalogPolicySource(
            new NyxIDUserServiceModelSourceIdentity("user-service-alpha"),
            "chrono-runtime",
            new ExplicitLLMModels(["gpt-5.5"]));
        catalog.GetScopeAsync("scope-alpha", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LLMModelCatalogView(
                LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
                LLMModelCatalogPolicyMode.Custom,
                true,
                31,
                new DateTimeOffset(2026, 8, 18, 3, 0, 0, TimeSpan.Zero),
                [source],
                LLMModelCatalogEffectiveSourceKind.Scope,
                [source],
                "mutation-alpha")));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            personalPreferences: personal,
            modelCatalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");
        host.Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "personal-token");

        var response = await host.Client.GetAsync("/api/ai/models");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("consistency").GetString().Should().Be("independent_authorities");
        var personalDefault = json.RootElement.GetProperty("personalDefault");
        personalDefault.GetProperty("authorityKind").GetString().Should().Be("authenticated_user");
        personalDefault.GetProperty("authorityStateVersion").ValueKind.Should().Be(JsonValueKind.Null);
        personalDefault.GetProperty("settings").GetProperty("selectionStatus").GetString()
            .Should().Be("system_default");

        var scopeCatalog = json.RootElement.GetProperty("scopeCatalog");
        scopeCatalog.GetProperty("scopeId").GetString().Should().Be("scope-alpha");
        scopeCatalog.GetProperty("authorityStateVersion").GetInt64().Should().Be(31);
        var policy = scopeCatalog.GetProperty("policy");
        policy.GetProperty("mode").GetString().Should().Be("custom_replace");
        policy.GetProperty("sources")[0].GetProperty("userServiceId").GetString()
            .Should().Be("user-service-alpha");
        policy.GetProperty("sources")[0].GetProperty("catalogServiceId").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Models_ShouldPreserveLegacyAndUnknownSourceIdentityWithoutInventingFacts()
    {
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        var legacySource = new LLMModelCatalogPolicySource(
            new NyxIDUserServiceModelSourceIdentity("user-service-legacy"),
            null,
            new ExplicitLLMModels(["legacy-model"]));
        var unknownSource = new LLMModelCatalogPolicySource(
            new UnknownModelSourceIdentity("future-service-alpha"),
            null,
            new ExplicitLLMModels(["future-model"]));
        catalog.GetScopeAsync("scope-alpha", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LLMModelCatalogView(
                LLMModelCatalogPolicyOwner.ForScope("scope-alpha"),
                LLMModelCatalogPolicyMode.Custom,
                true,
                32,
                new DateTimeOffset(2026, 8, 18, 4, 0, 0, TimeSpan.Zero),
                [legacySource, unknownSource],
                LLMModelCatalogEffectiveSourceKind.Scope,
                [legacySource, unknownSource],
                "mutation-legacy")));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            modelCatalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/models");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var sources = json.RootElement
            .GetProperty("scopeCatalog")
            .GetProperty("policy")
            .GetProperty("sources");
        sources[0].GetProperty("sourceId").GetString().Should().Be("user:user-service-legacy");
        sources[0].GetProperty("serviceSlugSnapshot").ValueKind.Should().Be(JsonValueKind.Null);
        sources[0].GetProperty("userServiceId").GetString().Should().Be("user-service-legacy");
        sources[1].GetProperty("sourceId").GetString().Should().Be("unsupported:future-service-alpha");
        sources[1].GetProperty("serviceSlugSnapshot").ValueKind.Should().Be(JsonValueKind.Null);
        sources[1].GetProperty("catalogServiceId").ValueKind.Should().Be(JsonValueKind.Null);
        sources[1].GetProperty("userServiceId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Models_WhenScopeCatalogFails_ShouldKeepPersonalSettingsAvailable()
    {
        var personal = Substitute.For<IUserLlmPreferenceService>();
        personal.GetSettingsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserLlmSettingsView(
                null,
                string.Empty,
                UserLlmSelectionStatus.SystemDefault,
                LLMModelCatalogDiagnosticKind.Unspecified,
                UserLlmRemediationKind.None,
                [],
                [],
                UserLlmCatalogStatus.Ready,
                new UserLlmSettingsCapabilities(false, false, false, false),
                null)));
        var catalog = Substitute.For<ILLMModelCatalogPolicyApplicationService>();
        catalog.GetScopeAsync("scope-alpha", Arg.Any<CancellationToken>())
            .Returns<Task<LLMModelCatalogView>>(_ => throw new LLMModelCatalogApplicationException(
                LLMModelCatalogApplicationErrorKind.Unavailable,
                "CATALOG_READ_UNAVAILABLE",
                "Catalog projection is unavailable."));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            personalPreferences: personal,
            modelCatalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/models");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetProperty("personalDefault").GetProperty("availability").GetString()
            .Should().Be("available");
        var scopeCatalog = json.RootElement.GetProperty("scopeCatalog");
        scopeCatalog.GetProperty("availability").GetString().Should().Be("unavailable");
        scopeCatalog.GetProperty("authorityStateVersion").ValueKind.Should().Be(JsonValueKind.Null);
        scopeCatalog.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("CATALOG_READ_UNAVAILABLE");
        scopeCatalog.GetProperty("error").GetProperty("message").GetString()
            .Should().Be("Scope model catalog is temporarily unavailable.");
    }

    [Fact]
    public async Task Activity_WhenConversationsFail_ShouldKeepRunsAvailable()
    {
        var chatHistory = Substitute.For<IChatHistoryQueryPort>();
        chatHistory.GetIndexAsync(Arg.Any<ChatHistoryIndexPageRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatHistoryIndexPage>>(_ => throw new InvalidOperationException("chat unavailable"));
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.ListActivityRunsForScopeAsync(
                "scope-alpha",
                Arg.Any<WorkflowActivityRunFeedFilter>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkflowActivityRunFeedPage
            {
                Items =
                [
                    new WorkflowActivityRunFeedRow
                    {
                        RunId = "run-alpha",
                        WorkflowId = "wf-alpha",
                        WorkflowName = "Alpha workflow",
                        ScopeId = "scope-alpha",
                        Status = "completed",
                        RunOrigin = "chat",
                        Success = true,
                        UpdatedAtUtc = DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
                        StateVersion = 19,
                    },
                ],
            }));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            chatHistory: chatHistory,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity?take=10");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("consistency").GetString()
            .Should().Be("independent_read_models");
        json.RootElement.GetProperty("conversations").GetProperty("availability").GetString()
            .Should().Be("unavailable");
        var runs = json.RootElement.GetProperty("runs");
        runs.GetProperty("availability").GetString().Should().Be("available");
        runs.GetProperty("items")[0].GetProperty("runId").GetString().Should().Be("run-alpha");
        runs.GetProperty("items")[0].GetProperty("authorityStateVersion").GetInt64().Should().Be(19);
    }

    [Fact]
    public async Task ActivityRuns_WithoutOptionalQuery_ShouldUseDefaults()
    {
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.ListActivityRunsForScopeAsync(
                "scope-alpha",
                Arg.Any<WorkflowActivityRunFeedFilter>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkflowActivityRunFeedPage()));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        await observatory.Received(1).ListActivityRunsForScopeAsync(
            "scope-alpha",
            Arg.Is<WorkflowActivityRunFeedFilter>(filter =>
                filter.Take == 50 &&
                !filter.IncludeTotalCount &&
                filter.Origins.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivityRuns_ShouldRedactLegacySensitiveSummariesAtTheApiBoundary()
    {
        const string inputSecret = "legacy-input-secret";
        const string stepSecret = "legacy-step-secret";
        const string failureSecret = "legacy-failure-secret";
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.ListActivityRunsForScopeAsync(
                "scope-alpha",
                Arg.Any<WorkflowActivityRunFeedFilter>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkflowActivityRunFeedPage
            {
                Items =
                [
                    new WorkflowActivityRunFeedRow
                    {
                        RunId = "run-alpha",
                        ScopeId = "scope-alpha",
                        InputSummary = $"{{\"app_token\":\"{inputSecret}\",\"mode\":\"preview\"}}",
                        CurrentStep = new WorkflowActivityRunStepSummary
                        {
                            StepId = "step-alpha",
                            InputSummary = $"password={stepSecret}",
                            Availability = "available",
                        },
                        FirstFailure = new WorkflowActivityRunFailureSummary
                        {
                            StepId = "step-alpha",
                            Message = $"provider failed token={failureSecret}",
                            Availability = "available",
                        },
                        UpdatedAtUtc = DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
                        StateVersion = 19,
                    },
                ],
            }));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain(inputSecret);
        body.Should().NotContain(stepSecret);
        body.Should().NotContain(failureSecret);
        using var json = JsonDocument.Parse(body);
        var run = json.RootElement.GetProperty("items")[0];
        run.GetProperty("inputSummary").GetString()
            .Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        run.GetProperty("currentStep").GetProperty("inputSummary").GetString()
            .Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        run.GetProperty("firstFailure").GetProperty("message").GetString()
            .Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
    }

    [Fact]
    public async Task ActivityRunDetailMapper_ShouldExposeTypedResultAndSanitizedFailure()
    {
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.GetRunForScopeAsync("scope-alpha", "run-alpha", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ObservatoryRunDetail?>(new ObservatoryRunDetail
            {
                Summary = new ObservatoryRunSummary
                {
                    RunId = "run-alpha",
                    WorkflowId = "wf-alpha",
                    ScopeId = "scope-alpha",
                    WorkflowName = "Alpha workflow",
                    Status = "failed",
                    CompletedAtUtc = DateTimeOffset.Parse("2026-08-18T03:59:58Z"),
                    DurationMs = 2_000,
                    UpdatedAtUtc = DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
                    StateVersion = 23,
                },
                FinalOutput = "Completed partial result.",
                FirstFailure = new WorkflowActivityRunFailureSummary
                {
                    StepId = "step-alpha",
                    Message = "Sanitized failure reason.",
                    Availability = "available",
                },
            }));
        var service = new AIWorkspaceActivityQueryService(
            Substitute.For<IChatHistoryQueryPort>(),
            observatory);

        var result = await service.GetRunAsync("scope-alpha", "run-alpha");

        result.Failure.Should().BeNull();
        result.Value.Should().NotBeNull();
        result.Value!.FinalOutput.Should().Be("Completed partial result.");
        result.Value.Summary.RunId.Should().Be("run-alpha");
        result.Value.Summary.WorkflowId.Should().Be("wf-alpha");
        result.Value.Summary.CompletedAtUtc.Should().Be(DateTimeOffset.Parse("2026-08-18T03:59:58Z"));
        result.Value.Summary.DurationMs.Should().Be(2_000);
        result.Value.Summary.FirstFailure.Should().NotBeNull();
        result.Value.Summary.FirstFailure!.StepId.Should().Be("step-alpha");
        result.Value.Summary.FirstFailure.Message.Should().Be("Sanitized failure reason.");
    }

    [Theory]
    [InlineData(
        ObservatoryRunDetailSectionVersionStatus.Unavailable,
        0,
        "",
        null,
        AIWorkspaceRunDetailSectionVersionStatus.Unavailable)]
    [InlineData(
        ObservatoryRunDetailSectionVersionStatus.VersionMismatch,
        21,
        "workflow-run-report.v1",
        "workflow-run-report.v1",
        AIWorkspaceRunDetailSectionVersionStatus.VersionMismatch)]
    public async Task ActivityRunDetailMapper_ShouldPreserveReportMaterializationStatus(
        ObservatoryRunDetailSectionVersionStatus sourceStatus,
        long sourceStateVersion,
        string reportVersion,
        string? expectedReportVersion,
        AIWorkspaceRunDetailSectionVersionStatus expectedStatus)
    {
        const long detailStateVersion = 23;
        const string reason = "Run report materialization is not aligned.";
        var reportSection = new ObservatoryRunDetailSectionVersion
        {
            DetailStateVersion = detailStateVersion,
            SourceStateVersion = sourceStateVersion,
            VersionStatus = sourceStatus,
            Reason = reason,
        };
        var alignedSection = new ObservatoryRunDetailSectionVersion
        {
            DetailStateVersion = detailStateVersion,
            SourceStateVersion = detailStateVersion,
            VersionStatus = ObservatoryRunDetailSectionVersionStatus.Aligned,
        };
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.GetRunForScopeAsync("scope-alpha", "run-alpha", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ObservatoryRunDetail?>(new ObservatoryRunDetail
            {
                Summary = new ObservatoryRunSummary
                {
                    RunId = "run-alpha",
                    ScopeId = "scope-alpha",
                    UpdatedAtUtc = DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
                    StateVersion = detailStateVersion,
                },
                ReportVersion = reportVersion,
                Sections = new ObservatoryRunDetailSectionVersions
                {
                    Overview = alignedSection,
                    Steps = reportSection,
                    Timeline = reportSection,
                    ExecutionPath = alignedSection,
                },
            }));
        var service = new AIWorkspaceActivityQueryService(
            Substitute.For<IChatHistoryQueryPort>(),
            observatory);

        var result = await service.GetRunAsync("scope-alpha", "run-alpha");

        result.Failure.Should().BeNull();
        result.Value.Should().NotBeNull();
        result.Value!.ReportVersion.Should().Be(expectedReportVersion);
        result.Value.Sections.Overview.VersionStatus.Should().Be(
            AIWorkspaceRunDetailSectionVersionStatus.Aligned);
        result.Value.Sections.Steps.VersionStatus.Should().Be(expectedStatus);
        result.Value.Sections.Steps.DetailStateVersion.Should().Be(detailStateVersion);
        result.Value.Sections.Steps.SourceStateVersion.Should().Be(sourceStateVersion);
        result.Value.Sections.Steps.Reason.Should().Be(reason);
        result.Value.Sections.Timeline.Should().Be(result.Value.Sections.Steps);
    }

    [Fact]
    public async Task ActivityRunDetail_ShouldExposeResultAndOmitInternalOrRawPayloads()
    {
        const string inputSecret = "legacy-detail-input-secret";
        const string outputSecret = "legacy-detail-output-secret";
        const string failureSecret = "legacy-detail-failure-secret";
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.GetRunForScopeAsync("scope-alpha", "run-alpha", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ObservatoryRunDetail?>(new ObservatoryRunDetail
            {
                Summary = new ObservatoryRunSummary
                {
                    RunId = "run-alpha",
                    WorkflowId = "wf-alpha",
                    ScopeId = "scope-alpha",
                    WorkflowName = "Alpha workflow",
                    Status = "completed",
                    CompletedAtUtc = DateTimeOffset.Parse("2026-08-18T03:59:58Z"),
                    DurationMs = 2_000,
                    UpdatedAtUtc = DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
                    StateVersion = 23,
                },
                ReportVersion = "workflow-run-report.v1",
                Sections = new ObservatoryRunDetailSectionVersions
                {
                    Overview = new ObservatoryRunDetailSectionVersion
                    {
                        DetailStateVersion = 23,
                        SourceStateVersion = 23,
                        VersionStatus = ObservatoryRunDetailSectionVersionStatus.Aligned,
                    },
                    Steps = new ObservatoryRunDetailSectionVersion
                    {
                        DetailStateVersion = 23,
                        SourceStateVersion = 19,
                        VersionStatus = ObservatoryRunDetailSectionVersionStatus.VersionMismatch,
                        Reason = "Run report artifact is stale.",
                    },
                    Timeline = new ObservatoryRunDetailSectionVersion
                    {
                        DetailStateVersion = 23,
                        SourceStateVersion = 0,
                        VersionStatus = ObservatoryRunDetailSectionVersionStatus.Unavailable,
                        Reason = "Run report artifact is unavailable.",
                    },
                    ExecutionPath = new ObservatoryRunDetailSectionVersion
                    {
                        DetailStateVersion = 23,
                        SourceStateVersion = 23,
                        VersionStatus = ObservatoryRunDetailSectionVersionStatus.Aligned,
                    },
                },
                InputSummary = $"{{\"api_key\":\"{inputSecret}\",\"mode\":\"preview\"}}",
                FinalOutput = $"Completed result token={outputSecret}",
                FinalError = "raw-provider-error-secret",
                FirstFailure = new WorkflowActivityRunFailureSummary
                {
                    StepId = "step-alpha",
                    Message = $"provider failed password={failureSecret}",
                    Availability = "available",
                },
                Operations =
                [
                    new ObservatoryOperationDetail
                    {
                        OperationId = "operation-alpha",
                        Kind = "tool_call",
                        RoleActorId = "actor-alpha",
                        ReasoningContent = "reasoning-secret",
                        ArgumentsJson = "{\"token\":\"argument-secret\"}",
                        ResultJson = "{\"token\":\"result-secret\"}",
                    },
                ],
                Timeline =
                [
                    new ObservatoryViewEvent
                    {
                        Kind = "tool_call",
                        TimestampUtc = DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
                        ToolCall = new ObservatoryToolCallDetail
                        {
                            ToolName = "lookup",
                            CallId = "call-alpha",
                            ArgumentsJson = "timeline-argument-secret",
                            ResultJson = "timeline-result-secret",
                            Success = true,
                        },
                    },
                ],
            }));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs/run-alpha");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("summary").GetProperty("runId").GetString().Should().Be("run-alpha");
        json.RootElement.GetProperty("summary").GetProperty("workflowId").GetString().Should().Be("wf-alpha");
        json.RootElement.GetProperty("summary").GetProperty("completedAtUtc").GetDateTimeOffset()
            .Should().Be(DateTimeOffset.Parse("2026-08-18T03:59:58Z"));
        json.RootElement.GetProperty("summary").GetProperty("durationMs").GetDouble().Should().Be(2_000);
        json.RootElement.GetProperty("summary").GetProperty("inputSummary").GetString()
            .Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        json.RootElement.GetProperty("finalOutput").GetString()
            .Should().Be($"Completed result token={WorkflowAuditTextSanitizer.RedactedValue}");
        json.RootElement.GetProperty("reportVersion").GetString().Should().Be("workflow-run-report.v1");
        var sections = json.RootElement.GetProperty("sections");
        sections.GetProperty("steps").GetProperty("versionStatus").GetString()
            .Should().Be("version_mismatch");
        sections.GetProperty("steps").GetProperty("sourceStateVersion").GetInt64().Should().Be(19);
        sections.GetProperty("timeline").GetProperty("versionStatus").GetString()
            .Should().Be("unavailable");
        sections.GetProperty("timeline").GetProperty("reason").GetString()
            .Should().Be("Run report artifact is unavailable.");
        json.RootElement.GetProperty("summary").GetProperty("firstFailure").GetProperty("message")
            .GetString().Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        body.Should().NotContain(inputSecret);
        body.Should().NotContain(outputSecret);
        body.Should().NotContain(failureSecret);
        body.Should().Contain("operation-alpha");
        body.Should().NotContain("reasoning-secret");
        body.Should().NotContain("argument-secret");
        body.Should().NotContain("result-secret");
        body.Should().NotContain("raw-provider-error-secret");
        body.Should().NotContain("actor-alpha");
    }

    [Fact]
    public async Task ActivityRunDetail_WhenNoOwnedRunIsReturned_ShouldReturnNotFound()
    {
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.GetRunForScopeAsync("scope-alpha", "run-private", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ObservatoryRunDetail?>(null));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs/run-private");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should().Contain("WORKFLOW_RUN_NOT_FOUND");
    }

    [Fact]
    public async Task ActivityRunDetail_WhenSourceFails_ShouldReturnServiceUnavailable()
    {
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.GetRunForScopeAsync("scope-alpha", "run-alpha", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ObservatoryRunDetail?>(
                new InvalidOperationException("source unavailable")));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            null,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs/run-alpha");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        body.Should().Contain("WORKFLOW_RUNS_UNAVAILABLE");
    }

    private static AgentProfileCatalogSnapshot Snapshot(
        AgentProfileOwner owner,
        long version,
        DateTimeOffset updatedAt,
        params AgentProfileCatalogEntry[] items) =>
        new(
            owner.OwnerCase == AgentProfileOwner.OwnerOneofCase.Scope
                ? $"profiles-scope-{owner.Scope.ScopeId}"
                : "profiles-system-aevatar",
            version,
            owner.Clone(),
            items,
            [],
            null,
            updatedAt);

    private static AgentProfileCatalogEntry Profile(
        string profileId,
        string slug,
        long publishedRevision,
        byte digestByte) =>
        new()
        {
            ProfileId = profileId,
            ProfileSlug = slug,
            ProfileActorId = $"actor-{profileId}",
            DisplayName = $"{slug} display",
            Purpose = $"{slug} purpose",
            Status = AgentProfileProvisioningStatus.Active,
            PublishedRevision = publishedRevision,
            SnapshotSha256 = publishedRevision > 0
                ? ByteString.CopyFrom(Enumerable.Repeat(digestByte, 32).ToArray())
                : ByteString.Empty,
        };

    private sealed record UnknownModelSourceIdentity(string Identity)
        : LLMModelSourceIdentity(Identity);

    private sealed class AIWorkspaceTestHost : IAsyncDisposable
    {
        private AIWorkspaceTestHost(WebApplication app, string? temporaryAssetsPath)
        {
            App = app;
            Client = app.GetTestClient();
            TemporaryAssetsPath = temporaryAssetsPath;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        private string? TemporaryAssetsPath { get; }

        public static async Task<AIWorkspaceTestHost> StartAsync(
            string? staticAssetsPath,
            IAgentProfileCatalogQueryPort? catalog = null,
            IUserLlmPreferenceService? personalPreferences = null,
            ILLMModelCatalogPolicyApplicationService? modelCatalog = null,
            IChatHistoryQueryPort? chatHistory = null,
            IWorkflowRunObservatoryQueryService? observatory = null)
        {
            string? temporaryAssetsPath = null;
            if (staticAssetsPath is null)
            {
                temporaryAssetsPath = Path.Combine(
                    Path.GetTempPath(),
                    $"aevatar-ai-workspace-{Guid.NewGuid():N}");
                Directory.CreateDirectory(temporaryAssetsPath);
                await File.WriteAllTextAsync(
                    Path.Combine(temporaryAssetsPath, "index.html"),
                    "<!doctype html><html><body data-ai-workspace-shell></body></html>");
                await File.WriteAllTextAsync(
                    Path.Combine(temporaryAssetsPath, "app.test.js"),
                    "globalThis.aiWorkspaceLoaded = true;");
                await File.WriteAllTextAsync(
                    Path.Combine(temporaryAssetsPath, "app.01234567.js"),
                    "globalThis.aiWorkspaceVersion = '01234567';");
                staticAssetsPath = temporaryAssetsPath;
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
                [$"{AIWorkspaceOptions.SectionName}:StaticAssetsPath"] = staticAssetsPath,
            });
            builder.Services.AddAIWorkspace(builder.Configuration);
            builder.Services.AddAuthentication("test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
            });
            builder.Services.AddSingleton(catalog ?? Substitute.For<IAgentProfileCatalogQueryPort>());
            builder.Services.AddSingleton(Substitute.For<IAgentProfileManagementQueryPort>());
            builder.Services.AddSingleton(Substitute.For<IAgentProfileExecutionQueryPort>());
            builder.Services.AddSingleton(Substitute.For<IAgentProfileActorPort>());
            builder.Services.AddSingleton(Substitute.For<IAgentProfileSkillSealer>());
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<AgentProfileApplicationService>();
            builder.Services.AddSingleton<IAgentProfileCatalogApplicationService>(serviceProvider =>
                serviceProvider.GetRequiredService<AgentProfileApplicationService>());
            builder.Services.AddSingleton(
                personalPreferences ?? Substitute.For<IUserLlmPreferenceService>());
            builder.Services.AddSingleton(
                modelCatalog ?? Substitute.For<ILLMModelCatalogPolicyApplicationService>());
            builder.Services.AddSingleton(chatHistory ?? Substitute.For<IChatHistoryQueryPort>());
            builder.Services.AddSingleton(observatory ?? Substitute.For<IWorkflowRunObservatoryQueryService>());

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapAIWorkspaceEndpoints();
            await app.StartAsync();
            return new AIWorkspaceTestHost(app, temporaryAssetsPath);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            if (TemporaryAssetsPath is not null && Directory.Exists(TemporaryAssetsPath))
                Directory.Delete(TemporaryAssetsPath, recursive: true);
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Scope") &&
                !Request.Headers.ContainsKey("X-Test-Authenticated"))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = Request.Headers["X-Test-Scope"]
                .ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static scopeId => new Claim("scope_id", scopeId));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
