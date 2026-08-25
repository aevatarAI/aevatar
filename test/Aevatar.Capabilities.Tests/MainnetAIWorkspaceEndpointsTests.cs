using System.Net;
using System.Security.Claims;
using System.Text;
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
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Application.Abstractions.Observatory;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        routes.Should().OnlyContain(route =>
            route.Pattern != null &&
            route.Pattern.StartsWith("/api/ai", StringComparison.Ordinal) &&
            route.IsAuthorized);
        routes.Select(static route => route.Pattern).Should().Contain([
            "/api/ai/context",
            "/api/ai/overview",
            "/api/ai/agents",
            "/api/ai/models",
            "/api/ai/models/personal-default",
            "/api/ai/models/catalog",
            "/api/ai/models/catalog/candidates",
            "/api/ai/models/catalog/candidates/{userServiceId}/models",
            "/api/ai/activity",
            "/api/ai/activity/conversations",
            "/api/ai/activity/runs",
            "/api/ai/activity/runs/{runId}",
        ]);
        routes.Select(static route => route.Pattern).Should().NotContain([
            "/ai",
            "/ai/{**path}",
            "/ai-assets/{**path}",
            "/chat",
            "/login",
            "/auth/callback",
            "/scopes",
            "/scopes/{**path}",
            "/settings",
        ]);
    }

    [Fact]
    public async Task Context_ShouldExposeAccountAndOnlyAIProductLinks()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync();
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/context");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        AssertNoAuthorizationPartitionFields(json.RootElement);
        json.RootElement.TryGetProperty("scopeId", out _).Should().BeFalse();
        json.RootElement.GetProperty("account").GetProperty("subject").GetString()
            .Should().Be("subject-alpha");
        json.RootElement.GetProperty("account").GetProperty("displayName").GetString()
            .Should().Be("Alpha User");
        json.RootElement.GetProperty("consistency").GetString().Should().Be("independent_read_models");
        json.RootElement.GetProperty("pages").GetProperty("agents").GetString().Should().Be("/ai#/agents");
        json.RootElement.GetProperty("pages").GetProperty("activity").GetString().Should().Be("/ai#/activity");
        var apis = json.RootElement.GetProperty("apis");
        apis.EnumerateObject().Should().OnlyContain(property =>
            property.Value.GetString() != null &&
            property.Value.GetString()!.StartsWith("/api/ai/", StringComparison.Ordinal));
        apis.GetProperty("overview").GetString().Should().Be("/api/ai/overview");
        apis.GetProperty("models").GetString().Should().Be("/api/ai/models");
        apis.GetProperty("personalModelDefault").GetString()
            .Should().Be("/api/ai/models/personal-default");
        apis.GetProperty("modelCatalog").GetString().Should().Be("/api/ai/models/catalog");
        apis.GetProperty("modelCandidates").GetString()
            .Should().Be("/api/ai/models/catalog/candidates");
        apis.GetProperty("activity").GetString().Should().Be("/api/ai/activity");
        apis.GetProperty("conversations").GetString().Should().Be("/api/ai/activity/conversations");
        apis.GetProperty("runs").GetString().Should().Be("/api/ai/activity/runs");
        var capabilities = json.RootElement.GetProperty("capabilities");
        capabilities.GetProperty("activity").GetProperty("page").GetString().Should().Be("/ai#/activity");
        capabilities.GetProperty("activity").GetProperty("api").GetString()
            .Should().Be("/api/ai/activity");
    }

    [Fact]
    public async Task Context_WithAmbiguousScopeClaims_ShouldFailClosed()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync();
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha,scope-beta");

        var response = await host.Client.GetAsync("/api/ai/context");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AI_ACCESS_CONTEXT_REQUIRED");
    }

    [Fact]
    public async Task Context_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync();

        var response = await host.Client.GetAsync("/api/ai/context");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(body);
        AssertStrictError(json.RootElement, "AI_AUTHENTICATION_REQUIRED");
    }

    [Theory]
    [InlineData("{\"routeValue\":")]
    [InlineData("{\"routeValue\":\"route-a\",\"modelId\":null,\"scopeId\":\"scope-secret\"}")]
    public async Task ModelMutation_WithMalformedOrUnknownJson_ShouldReturnStrictAIError(string payload)
    {
        await using var host = await AIWorkspaceTestHost.StartAsync();
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await host.Client.PutAsync("/api/ai/models/personal-default", content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(body);
        AssertStrictError(json.RootElement, "AI_REQUEST_INVALID");
        body.Should().NotContain("scope-secret");
    }

    [Fact]
    public async Task ActivityRuns_WithInvalidTypedQuery_ShouldReturnStrictAIError()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync();
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs?take=not-an-integer");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(body);
        AssertStrictError(json.RootElement, "AI_REQUEST_INVALID");
        body.Should().NotContain("not-an-integer");
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/api/aix/context")]
    public async Task ErrorContractMiddleware_ForNonAIPaths_ShouldLeaveResponseUntouched(string path)
    {
        await using var body = new MemoryStream();
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Response.Body = body;
        var middleware = new AIWorkspaceErrorContractMiddleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        body.Length.Should().Be(0);
        http.Response.ContentType.Should().BeNull();
    }

    [Fact]
    public async Task Context_WithAuthenticatedPrincipalWithoutScope_ShouldFailClosed()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync();
        host.Client.DefaultRequestHeaders.Add("X-Test-Authenticated", "true");

        var response = await host.Client.GetAsync("/api/ai/context");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("AI_ACCESS_CONTEXT_REQUIRED");
    }

    [Fact]
    public async Task Context_WithAuthenticatedScopeButWithoutSubject_ShouldFailClosed()
    {
        await using var host = await AIWorkspaceTestHost.StartAsync();
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");
        host.Client.DefaultRequestHeaders.Add("X-Test-No-Subject", "true");

        var response = await host.Client.GetAsync("/api/ai/context");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("AI_SUBJECT_REQUIRED");
    }

    [Fact]
    public async Task QueryEndpoints_WhenDownstreamFailureContainsPartitionVocabulary_ShouldReturnStableAIError()
    {
        const string internalCode = "SCOPE_OWNER_AUTHORITY_FAILURE";
        const string internalMessage =
            "scopeId scope-secret belongs to Team team-secret and ownerKind scope.";
        AIWorkspaceQueryResult<T> Failure<T>(AIWorkspaceQueryFailureKind kind) =>
            AIWorkspaceQueryResult<T>.Fail(kind, internalCode, internalMessage);

        var overview = Substitute.For<IAIWorkspaceOverviewQueryService>();
        overview.QueryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failure<AIWorkspaceOverviewView>(
                AIWorkspaceQueryFailureKind.InvalidInput)));
        var agents = Substitute.For<IAIWorkspaceAgentsQueryService>();
        agents.QueryAsync(
                Arg.Any<string>(),
                Arg.Any<AIWorkspaceAgentsQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failure<AIWorkspaceAgentsView>(
                AIWorkspaceQueryFailureKind.InvalidCursor)));
        var activity = Substitute.For<IAIWorkspaceActivityQueryService>();
        activity.QueryAsync(
                Arg.Any<string>(),
                Arg.Any<AIWorkspaceActivityQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failure<AIWorkspaceActivityView>(
                AIWorkspaceQueryFailureKind.Unavailable)));
        activity.QueryConversationsAsync(
                Arg.Any<string>(),
                Arg.Any<AIWorkspacePageQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failure<AIWorkspaceConversationCollectionView>(
                AIWorkspaceQueryFailureKind.InvalidCursor)));
        activity.QueryRunsAsync(
                Arg.Any<string>(),
                Arg.Any<AIWorkspaceRunsQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failure<AIWorkspaceRunCollectionView>(
                AIWorkspaceQueryFailureKind.InvalidInput)));
        activity.GetRunAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failure<AIWorkspaceRunDetailView>(
                AIWorkspaceQueryFailureKind.NotFound)));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            agentsQuery: agents,
            activityQuery: activity,
            overviewQuery: overview);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        foreach (var endpoint in new[]
                 {
                     (Path: "/api/ai/overview",
                         Status: HttpStatusCode.BadRequest,
                         Code: "AI_REQUEST_INVALID"),
                     (Path: "/api/ai/agents",
                         Status: HttpStatusCode.BadRequest,
                         Code: "AI_CURSOR_INVALID"),
                     (Path: "/api/ai/activity",
                         Status: HttpStatusCode.ServiceUnavailable,
                         Code: "AI_WORKSPACE_UNAVAILABLE"),
                     (Path: "/api/ai/activity/conversations",
                         Status: HttpStatusCode.BadRequest,
                         Code: "AI_CURSOR_INVALID"),
                     (Path: "/api/ai/activity/runs",
                         Status: HttpStatusCode.BadRequest,
                         Code: "AI_REQUEST_INVALID"),
                     (Path: "/api/ai/activity/runs/run-alpha",
                         Status: HttpStatusCode.NotFound,
                         Code: "AI_RESOURCE_NOT_FOUND"),
                 })
        {
            var response = await host.Client.GetAsync(endpoint.Path);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(endpoint.Status, body);
            using var json = JsonDocument.Parse(body);
            AssertStrictError(json.RootElement, endpoint.Code);
            body.Should().NotContain(internalCode);
            body.Should().NotContain("scope-secret");
            body.Should().NotContain("team-secret");
            body.Should().NotContain("ownerKind");
        }
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
            catalog,
            chatHistory: chatHistory,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/overview?take=5");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        AssertNoAuthorizationPartitionFields(json.RootElement);
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
            catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/agents?take=10");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        AssertNoAuthorizationPartitionFields(json.RootElement);
        var owned = json.RootElement.GetProperty("owned");
        owned.GetProperty("authorityStateVersion").GetInt64().Should().Be(11);
        owned.GetProperty("items").GetArrayLength().Should().Be(2);
        owned.GetProperty("items")[0].GetProperty("published").GetBoolean().Should().BeTrue();
        owned.GetProperty("items")[1].GetProperty("published").GetBoolean().Should().BeFalse();
        owned.GetProperty("items")[0].GetProperty("publishedSnapshotSha256").GetString()
            .Should().Be(new string('1', 64));

        var templates = json.RootElement.GetProperty("systemTemplates");
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
    public async Task Models_ShouldKeepPersonalAndCatalogAuthoritiesIndependent()
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
            personalPreferences: personal,
            modelCatalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");
        host.Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "personal-token");

        var response = await host.Client.GetAsync("/api/ai/models");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        AssertNoAuthorizationPartitionFields(json.RootElement);
        json.RootElement.GetProperty("consistency").GetString().Should().Be("independent_authorities");
        var personalDefault = json.RootElement.GetProperty("personalDefault");
        personalDefault.GetProperty("authorityStateVersion").ValueKind.Should().Be(JsonValueKind.Null);
        personalDefault.GetProperty("settings").GetProperty("selectionStatus").GetString()
            .Should().Be("system_default");

        var catalogView = json.RootElement.GetProperty("catalog");
        catalogView.GetProperty("authorityStateVersion").GetInt64().Should().Be(31);
        var policy = catalogView.GetProperty("policy");
        policy.GetProperty("mode").GetString().Should().Be("custom_replace");
        policy.GetProperty("effectiveSource").GetString().Should().Be("custom");
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
            modelCatalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/models");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var sources = json.RootElement
            .GetProperty("catalog")
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
    public async Task Models_WhenCatalogFails_ShouldKeepPersonalSettingsAvailable()
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
            personalPreferences: personal,
            modelCatalog: catalog);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/models");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.RootElement.GetProperty("personalDefault").GetProperty("availability").GetString()
            .Should().Be("available");
        var catalogView = json.RootElement.GetProperty("catalog");
        catalogView.GetProperty("availability").GetString().Should().Be("unavailable");
        catalogView.GetProperty("authorityStateVersion").ValueKind.Should().Be(JsonValueKind.Null);
        catalogView.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("MODEL_CATALOG_UNAVAILABLE");
        catalogView.GetProperty("error").GetProperty("message").GetString()
            .Should().Be("Model catalog is temporarily unavailable.");
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
            chatHistory: chatHistory,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity?take=10");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        AssertNoAuthorizationPartitionFields(json.RootElement);
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
    public async Task ActivitySourceEndpoints_WhenUnavailable_ShouldReturnStrictErrors()
    {
        var chatHistory = Substitute.For<IChatHistoryQueryPort>();
        chatHistory.GetIndexAsync(Arg.Any<ChatHistoryIndexPageRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatHistoryIndexPage>>(_ => throw new InvalidOperationException("internal conversation source"));
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.ListActivityRunsForScopeAsync(
                "scope-alpha",
                Arg.Any<WorkflowActivityRunFeedFilter>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<WorkflowActivityRunFeedPage>>(_ => throw new InvalidOperationException("internal run source"));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            chatHistory: chatHistory,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var conversations = await host.Client.GetAsync("/api/ai/activity/conversations");
        var runs = await host.Client.GetAsync("/api/ai/activity/runs");

        conversations.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var conversationError = JsonDocument.Parse(await conversations.Content.ReadAsStringAsync());
        AssertStrictError(conversationError.RootElement, "CONVERSATIONS_UNAVAILABLE");
        runs.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var runError = JsonDocument.Parse(await runs.Content.ReadAsStringAsync());
        AssertStrictError(runError.RootElement, "WORKFLOW_RUNS_UNAVAILABLE");
    }

    [Fact]
    public async Task ActivitySourceEndpoints_WhenSourceErrorContainsPartitionVocabulary_ShouldIgnoreIt()
    {
        const string internalCode = "SCOPE_ACTIVITY_AUTHORITY_FAILURE";
        const string internalMessage =
            "scopeId scope-secret belongs to Team team-secret and ownerKind scope.";
        var activity = Substitute.For<IAIWorkspaceActivityQueryService>();
        activity.QueryConversationsAsync(
                Arg.Any<string>(),
                Arg.Any<AIWorkspacePageQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AIWorkspaceQueryResult<AIWorkspaceConversationCollectionView>.Success(
                new AIWorkspaceConversationCollectionView(
                    "internal_conversation_source",
                    AIWorkspaceSourceAvailability.Unavailable,
                    [],
                    null,
                    new AIWorkspaceSourceErrorView(internalCode, internalMessage)))));
        activity.QueryRunsAsync(
                Arg.Any<string>(),
                Arg.Any<AIWorkspaceRunsQuery>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AIWorkspaceQueryResult<AIWorkspaceRunCollectionView>.Success(
                new AIWorkspaceRunCollectionView(
                    "internal_run_source",
                    AIWorkspaceSourceAvailability.Unavailable,
                    [],
                    null,
                    false,
                    null,
                    new AIWorkspaceSourceErrorView(internalCode, internalMessage)))));
        await using var host = await AIWorkspaceTestHost.StartAsync(activityQuery: activity);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var conversations = await host.Client.GetAsync("/api/ai/activity/conversations");
        var conversationBody = await conversations.Content.ReadAsStringAsync();
        var runs = await host.Client.GetAsync("/api/ai/activity/runs");
        var runBody = await runs.Content.ReadAsStringAsync();

        conversations.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, conversationBody);
        using var conversationError = JsonDocument.Parse(conversationBody);
        AssertStrictError(conversationError.RootElement, "CONVERSATIONS_UNAVAILABLE");
        runs.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, runBody);
        using var runError = JsonDocument.Parse(runBody);
        AssertStrictError(runError.RootElement, "WORKFLOW_RUNS_UNAVAILABLE");
        (conversationBody + runBody).Should().NotContain(internalCode);
        (conversationBody + runBody).Should().NotContain("scope-secret");
        (conversationBody + runBody).Should().NotContain("team-secret");
        (conversationBody + runBody).Should().NotContain("ownerKind");
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
    public async Task Activity_ShouldMapInternalClassificationsToClosedNeutralValues()
    {
        const string internalServiceKind = "aevatar-console.team-chat";
        var chatHistory = Substitute.For<IChatHistoryQueryPort>();
        chatHistory.GetIndexAsync(Arg.Any<ChatHistoryIndexPageRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatHistoryIndexPage(
                [
                    new ConversationMeta(
                        "conversation-alpha",
                        "Alpha conversation",
                        "service-alpha",
                        internalServiceKind,
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
                        RunOrigin = WorkflowRunOrigins.TeamInvoke,
                        UpdatedAtUtc = DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
                        StateVersion = 19,
                    },
                ],
            }));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            chatHistory: chatHistory,
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var conversation = json.RootElement.GetProperty("conversations").GetProperty("items")[0];
        conversation.GetProperty("conversationKind").GetString().Should().Be("other");
        conversation.TryGetProperty("serviceKind", out _).Should().BeFalse();
        conversation.TryGetProperty("serviceId", out _).Should().BeFalse();
        json.RootElement.GetProperty("runs").GetProperty("items")[0]
            .GetProperty("runOrigin").GetString().Should().Be("interactive");
        AssertStringValuesDoNotContain(
            json.RootElement,
            internalServiceKind,
            WorkflowRunOrigins.TeamInvoke);
    }

    [Fact]
    public async Task ActivityRuns_WithNeutralOriginFilters_ShouldTranslateToInternalOrigins()
    {
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.ListActivityRunsForScopeAsync(
                "scope-alpha",
                Arg.Any<WorkflowActivityRunFeedFilter>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new WorkflowActivityRunFeedPage()));
        await using var host = await AIWorkspaceTestHost.StartAsync(observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync(
            "/api/ai/activity/runs?origins=interactive,integration,automation,development,interactive");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        await observatory.Received(1).ListActivityRunsForScopeAsync(
            "scope-alpha",
            Arg.Is<WorkflowActivityRunFeedFilter>(filter => filter.Origins.SequenceEqual(new[]
            {
                WorkflowRunOrigins.MemberInvoke,
                WorkflowRunOrigins.DefaultInvoke,
                WorkflowRunOrigins.TeamInvoke,
                WorkflowRunOrigins.AdHocChat,
                "chat",
                WorkflowRunOrigins.ServiceInvoke,
                WorkflowRunOrigins.Webhook,
                WorkflowRunOrigins.WorkOrder,
                WorkflowRunOrigins.Provisioned,
                WorkflowRunOrigins.Draft,
            })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivityRuns_WithInteractiveOriginFilter_ShouldIncludeHistoricalChatRows()
    {
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.ListActivityRunsForScopeAsync(
                "scope-alpha",
                Arg.Any<WorkflowActivityRunFeedFilter>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var filter = callInfo.ArgAt<WorkflowActivityRunFeedFilter>(1);
                return Task.FromResult(new WorkflowActivityRunFeedPage
                {
                    Items = filter.Origins.Contains("chat", StringComparer.Ordinal)
                        ? [
                            new WorkflowActivityRunFeedRow
                            {
                                RunId = "run-historical-chat",
                                WorkflowName = "Historical chat workflow",
                                RunOrigin = "chat",
                                UpdatedAtUtc = DateTimeOffset.Parse("2026-08-18T04:00:00Z"),
                                StateVersion = 19,
                            },
                        ]
                        : [],
                });
            });
        await using var host = await AIWorkspaceTestHost.StartAsync(observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs?origins=interactive");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var run = json.RootElement.GetProperty("items").EnumerateArray().Should().ContainSingle().Which;
        run.GetProperty("runId").GetString().Should().Be("run-historical-chat");
        run.GetProperty("runOrigin").GetString().Should().Be("interactive");
        await observatory.Received(1).ListActivityRunsForScopeAsync(
            "scope-alpha",
            Arg.Is<WorkflowActivityRunFeedFilter>(filter => filter.Origins.SequenceEqual(new[]
            {
                WorkflowRunOrigins.MemberInvoke,
                WorkflowRunOrigins.DefaultInvoke,
                WorkflowRunOrigins.TeamInvoke,
                WorkflowRunOrigins.AdHocChat,
                "chat",
            })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivityRuns_WithInternalOriginFilter_ShouldRejectItAtTheApiBoundary()
    {
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        await using var host = await AIWorkspaceTestHost.StartAsync(observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync(
            $"/api/ai/activity/runs?origins={WorkflowRunOrigins.TeamInvoke}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("code").GetString().Should().Be("INVALID_ACTIVITY_ORIGIN");
        AssertStringValuesDoNotContain(json.RootElement, WorkflowRunOrigins.TeamInvoke);
        await observatory.DidNotReceive().ListActivityRunsForScopeAsync(
            Arg.Any<string>(),
            Arg.Any<WorkflowActivityRunFeedFilter>(),
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
    [InlineData(
        ObservatoryRunDetailSectionVersionStatus.Disabled,
        0,
        "workflow-run-report.v1",
        "workflow-run-report.v1",
        AIWorkspaceRunDetailSectionVersionStatus.Disabled)]
    public async Task ActivityRunDetailMapper_ShouldPreserveSectionMaterializationStatus(
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
                    RunOrigin = WorkflowRunOrigins.TeamInvoke,
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
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs/run-alpha");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        AssertNoAuthorizationPartitionFields(json.RootElement);
        json.RootElement.GetProperty("summary").GetProperty("runId").GetString().Should().Be("run-alpha");
        json.RootElement.GetProperty("summary").GetProperty("workflowId").GetString().Should().Be("wf-alpha");
        json.RootElement.GetProperty("summary").GetProperty("completedAtUtc").GetDateTimeOffset()
            .Should().Be(DateTimeOffset.Parse("2026-08-18T03:59:58Z"));
        json.RootElement.GetProperty("summary").GetProperty("durationMs").GetDouble().Should().Be(2_000);
        json.RootElement.GetProperty("summary").GetProperty("runOrigin").GetString()
            .Should().Be("interactive");
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
        body.Should().NotContain(WorkflowRunOrigins.TeamInvoke);
    }

    [Fact]
    public async Task ActivityRunDetail_WhenNoOwnedRunIsReturned_ShouldReturnNotFound()
    {
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.GetRunForScopeAsync("scope-alpha", "run-private", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ObservatoryRunDetail?>(null));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs/run-private");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should().Contain("AI_RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task ActivityRunDetail_WhenSourceFails_ShouldReturnServiceUnavailable()
    {
        var observatory = Substitute.For<IWorkflowRunObservatoryQueryService>();
        observatory.GetRunForScopeAsync("scope-alpha", "run-alpha", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ObservatoryRunDetail?>(
                new InvalidOperationException("source unavailable")));
        await using var host = await AIWorkspaceTestHost.StartAsync(
            observatory: observatory);
        host.Client.DefaultRequestHeaders.Add("X-Test-Scope", "scope-alpha");

        var response = await host.Client.GetAsync("/api/ai/activity/runs/run-alpha");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, body);
        body.Should().Contain("AI_WORKSPACE_UNAVAILABLE");
    }

    private static void AssertNoAuthorizationPartitionFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                new[]
                {
                    "scopeId",
                    "ownerKind",
                    "authorityKind",
                    "scopeCatalog",
                }.Should().NotContain(property.Name);
                AssertNoAuthorizationPartitionFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AssertNoAuthorizationPartitionFields(item);
        }
    }

    private static void AssertStrictError(JsonElement error, string expectedCode)
    {
        error.EnumerateObject().Select(static property => property.Name)
            .Should().BeEquivalentTo("code", "message");
        error.GetProperty("code").GetString().Should().Be(expectedCode);
        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static void AssertStringValuesDoNotContain(
        JsonElement element,
        params string[] forbiddenValues)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    AssertStringValuesDoNotContain(property.Value, forbiddenValues);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AssertStringValuesDoNotContain(item, forbiddenValues);
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                foreach (var forbiddenValue in forbiddenValues)
                {
                    value.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase)
                        .Should().BeFalse($"JSON string values must not expose '{forbiddenValue}'");
                }
                break;
        }
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
        private AIWorkspaceTestHost(WebApplication app)
        {
            App = app;
            Client = app.GetTestClient();
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public static async Task<AIWorkspaceTestHost> StartAsync(
            IAgentProfileCatalogQueryPort? catalog = null,
            IUserLlmPreferenceService? personalPreferences = null,
            ILLMModelCatalogPolicyApplicationService? modelCatalog = null,
            IChatHistoryQueryPort? chatHistory = null,
            IWorkflowRunObservatoryQueryService? observatory = null,
            IAIWorkspaceAgentsQueryService? agentsQuery = null,
            IAIWorkspaceActivityQueryService? activityQuery = null,
            IAIWorkspaceOverviewQueryService? overviewQuery = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Enabled"] = "true",
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
            builder.Services.AddSingleton(Substitute.For<IUserConfigService>());
            builder.Services.AddSingleton(
                modelCatalog ?? Substitute.For<ILLMModelCatalogPolicyApplicationService>());
            builder.Services.AddSingleton(chatHistory ?? Substitute.For<IChatHistoryQueryPort>());
            builder.Services.AddSingleton(observatory ?? Substitute.For<IWorkflowRunObservatoryQueryService>());
            if (agentsQuery is not null)
                builder.Services.AddSingleton(agentsQuery);
            if (activityQuery is not null)
                builder.Services.AddSingleton(activityQuery);
            if (overviewQuery is not null)
                builder.Services.AddSingleton(overviewQuery);

            var app = builder.Build();
            app.UseAIWorkspaceErrorContract();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapAIWorkspaceEndpoints();
            await app.StartAsync();
            return new AIWorkspaceTestHost(app);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
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

            IEnumerable<Claim> claims = Request.Headers["X-Test-Scope"]
                .ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static scopeId => new Claim("scope_id", scopeId));
            if (!Request.Headers.ContainsKey("X-Test-No-Subject"))
            {
                claims = claims.Concat([
                    new Claim("sub", "subject-alpha"),
                    new Claim("preferred_username", "Alpha User"),
                ]);
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
