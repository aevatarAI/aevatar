using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using StudioUserConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderToolTests
{
    [Fact]
    public async Task ExecuteAsync_ListTemplates_ReturnsDailyReportTemplate()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>());
        services.AddSingleton(Substitute.For<IActorRuntime>());
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));

        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list_templates"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("templates").EnumerateArray()
                .Any(static x => x.GetProperty("name").GetString() == "daily_report")
                .Should().BeTrue();
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_RejectsGroupChats()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>());
        services.AddSingleton(Substitute.For<IActorRuntime>());
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));

        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "group",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *"
                }
                """);

            result.Should().Contain("private chat");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DispatchesInitializeAndImmediateTrigger()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active",
                  "connected_at":"2026-04-15T00:00:00Z"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-1","full_key":"full-key-1"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            [ChannelMetadataKeys.SenderId] = "ou_user_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-1",
                  "github_username": "alice",
                  "repositories": "aevatarAI/aevatar",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "run_immediately": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("skill-runner-1");
            doc.RootElement.GetProperty("api_key_id").GetString().Should().Be("key-1");
            doc.RootElement.GetProperty("github_username").GetString().Should().Be("alice");
            doc.RootElement.GetProperty("run_immediately_requested").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("github_username_preference_saved").GetBoolean().Should().BeFalse();

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(InitializeSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().TemplateName == "daily_report" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().ScopeId == "scope-1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.ConversationId == "oc_chat_1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.NyxProviderSlug == "api-lark-bot" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.NyxApiKey == "full-key-1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.ApiKeyId == "key-1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.OwnerNyxUserId == "user-1" &&
                    // p2p inbound without LarkUnionId in the request context falls back to the
                    // sender open_id. Lark accepts this only when the relay-side and outbound
                    // apps match; cross-app deployments must populate LarkUnionId at ingress
                    // (see test below) to avoid `code:99992361 open_id cross app` rejections.
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.LarkReceiveId == "ou_user_1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.LarkReceiveIdType == "open_id"),
                Arg.Any<CancellationToken>());

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor) &&
                    e.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason == "create_agent"),
                Arg.Any<CancellationToken>());

            var apiKeyRequest = handler.Requests.Should()
                .ContainSingle(x => x.Method == HttpMethod.Post && x.Path == "/api/v1/api-keys")
                .Subject;
            using var apiKeyDoc = JsonDocument.Parse(apiKeyRequest.Body!);
            apiKeyDoc.RootElement.GetProperty("allowed_service_ids").EnumerateArray()
                .Select(static item => item.GetString())
                .Should()
                .BeEquivalentTo(["svc-github", "svc-lark"]);
            apiKeyDoc.RootElement.TryGetProperty("allow_all_services", out _).Should().BeFalse();
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_PinsLarkChatId_When_RelayPropagatesIt()
    {
        // The new outbound priority pins (chat_id, "chat_id") whenever the relay surfaces
        // ChannelMetadataKeys.LarkChatId — chat_id is the literal DM thread, no user-id
        // translation is needed. This is the integration counterpart of
        // LarkConversationTargetsTests.BuildFromInbound_ShouldPreferLarkChatId_ForP2pDirectMessages
        // and is what survives both `99992361 open_id cross app` (PR #403/409) and
        // `99992364 user id cross tenant` (PR after #409) failure modes in production.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-union-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-union-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-union-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-union-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-union-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-union-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active",
                  "connected_at":"2026-04-15T00:00:00Z"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-union-1","full_key":"full-key-union-1"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_dm_chat_1",
            [ChannelMetadataKeys.SenderId] = "ou_user_1",
            [ChannelMetadataKeys.LarkUnionId] = "on_user_1",
            [ChannelMetadataKeys.LarkChatId] = "oc_dm_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-union-1",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(InitializeSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.LarkReceiveId == "oc_dm_chat_1" &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().OutboundConfig.LarkReceiveIdType == "chat_id"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_FailsClosed_When_GithubProxyDeniedForNewKey()
    {
        // Issue aevatarAI/aevatar#411 + #417: the create flow preflights GitHub proxy access
        // with the freshly minted agent API key. Originally (#411) the failure mode this caught
        // was misdiagnosed as a missing api-key→GitHub binding; #417 fixed that root cause by
        // populating `allowed_service_ids` with per-user `UserService.id`s instead of catalog
        // ids. The probe is retained because GitHub OAuth grants can still be revoked outside
        // our control (user clicks "Revoke access" at GitHub, scopes downgraded, account
        // temp-banned). Surfacing the 401/403 at create-time avoids persisting an agent that
        // would produce empty output on every scheduled run.
        //
        // Pinned in this test: the structured `github_proxy_access_denied` error is returned
        // (no actor invocation), AND the freshly minted api-key IS revoked so retries don't
        // accumulate orphan proxy-scoped keys (codex review PR #418 r3141846175).
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-github-403");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-github-403").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-github-403", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active",
                  "connected_at":"2026-04-15T00:00:00Z"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-403","full_key":"full-key-403"}""");
        // The preflight: `NyxIdApiClient.SendAsync` wraps any HTTP non-2xx as
        // `{"error": true, "status": <http>, "body": "<raw downstream body>"}` (NyxIdApiClient.cs:680).
        // Reviewer (PR #412 r3141699476) caught that the previous handler shape used `"code"`
        // but real production uses `"status"` — mirror the actual envelope so the parser is
        // exercised against what runtime delivers, not a synthetic shape.
        handler.Add(HttpMethod.Get, "/api/v1/proxy/s/api-github/rate_limit",
            """{"error": true, "status": 403, "body": "{\"message\":\"Bad credentials\",\"documentation_url\":\"https://docs.github.com/rest\"}"}""");
        // Codex review (PR #418 r3141846175): retries of `/daily` mint a new api-key on every
        // run. Without best-effort revoke on preflight failure, the user's NyxID account would
        // accumulate one orphan proxy-scoped key per failed retry. Stub the DELETE so the test
        // can verify the revoke fires.
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-403", """{"deleted":true}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            [ChannelMetadataKeys.SenderId] = "ou_user_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-github-403",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("github_proxy_access_denied");
            doc.RootElement.GetProperty("http_status").GetInt32().Should().Be(403);
            // The hint should point users at re-authorizing the GitHub provider at NyxID, not
            // at api-key bindings (which used to be the misdiagnosis under #411 — see #417).
            doc.RootElement.GetProperty("hint").GetString().Should().Contain("Re-authorize");

            // The actor must NOT receive InitializeSkillRunnerCommand — preflight aborts
            // BEFORE the actor is invoked so we don't leave a broken agent in the catalog.
            await skillRunnerActor.DidNotReceive().HandleEventAsync(
                Arg.Any<EventEnvelope>(),
                Arg.Any<CancellationToken>());

            // Codex review (PR #418 r3141846175): even though the api-key carries the right
            // `allowed_service_ids` under #417, the create flow mints a *new* key per run.
            // Without best-effort revoke on preflight failure, every failed `/daily` retry
            // would orphan one proxy-scoped key in the user's NyxID account. Pin that the
            // DELETE fires so we don't regress on this cleanup.
            handler.Requests.Should().Contain(r =>
                r.Method == HttpMethod.Delete &&
                r.Path == "/api/v1/api-keys/key-403");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_LogsFallbackBreadcrumb_When_LarkUnionIdMissing()
    {
        // Reviewer (PR #409 r3141562097): when the relay does not surface LarkUnionId at agent
        // creation, BuildFromInbound returns (ou_*, open_id, FellBack=true). The flag itself is
        // not persisted on OutboundConfig (typed receive id/type only), so a downstream
        // LarkConversationTargets.Resolve() at SkillRunner send time sees populated typed fields
        // and reports FellBack=false — meaning the cross-app risk is invisible to operators
        // unless the agent-create site logs it once. Pin the LogDebug breadcrumb so the
        // observability promised in the PR description actually fires in production.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-fallback-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-fallback-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-fallback-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-fallback-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-fallback-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-fallback-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active",
                  "connected_at":"2026-04-15T00:00:00Z"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-fallback-1","full_key":"full-key-fallback-1"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);

        var logger = new ListLogger<AgentBuilderTool>();
        var tool = new AgentBuilderTool(services.BuildServiceProvider(), logger);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            [ChannelMetadataKeys.SenderId] = "ou_user_1",
            // Deliberately NO LarkUnionId / LarkChatId — this is the cross-app risky path.
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-fallback-1",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");

            // The breadcrumb must capture enough context to correlate with downstream Lark
            // `99992361` rejections: agent_id, the missing typed fields, and the chosen receive
            // type. Otherwise operators get no signal and the silent-default bug class re-opens.
            var fallback = logger.Entries.Should().ContainSingle(entry =>
                entry.Level == LogLevel.Debug &&
                entry.Message.Contains("Agent builder fell back to legacy delivery target inference") &&
                entry.Message.Contains("skill-runner-fallback-1") &&
                entry.Message.Contains("hasUnionId=False") &&
                entry.Message.Contains("hasLarkChatId=False") &&
                entry.Message.Contains("hasSenderId=True") &&
                entry.Message.Contains("resolvedReceiveIdType=open_id")).Subject;
            fallback.Message.Should().Contain("99992361");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_DoesNotLogFallback_When_LarkUnionIdPresent()
    {
        // Counterpart to the breadcrumb test: when the relay surfaces union_id, the typed
        // delivery target is cross-app safe and we must NOT spam Debug logs on every successful
        // ingress (otherwise the breadcrumb signal becomes useless noise once /agents traffic
        // ramps up).
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-no-fallback-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-no-fallback-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-no-fallback-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-no-fallback-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-no-fallback-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-no-fallback-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active",
                  "connected_at":"2026-04-15T00:00:00Z"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-no-fallback-1","full_key":"full-key-no-fallback-1"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);

        var logger = new ListLogger<AgentBuilderTool>();
        var tool = new AgentBuilderTool(services.BuildServiceProvider(), logger);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            [ChannelMetadataKeys.SenderId] = "ou_user_1",
            [ChannelMetadataKeys.LarkUnionId] = "on_user_1",
            [ChannelMetadataKeys.LarkChatId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-no-fallback-1",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            logger.Entries.Should().NotContain(entry =>
                entry.Message.Contains("fell back to legacy delivery target inference"));
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_UsesSavedGithubUsernamePreference_WhenArgumentMissing()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-pref-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-pref-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-pref-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-pref-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-pref-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-pref-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        userConfigQueryPort.GetAsync("scope-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StudioUserConfig(string.Empty, GithubUsername: "saved-user")));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-pref-1","full_key":"full-key-pref-1"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(userConfigQueryPort);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-pref-1",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(InitializeSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().SkillContent.Contains("Primary GitHub username: saved-user", StringComparison.Ordinal) &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().ExecutionPrompt.Contains("saved-user", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());

            handler.Requests.Should().NotContain(x => x.Path == "/api/v1/proxy/s/api-github/user");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_DerivesGithubUsername_FromNyxProxy_WhenArgumentAndPreferenceMissing()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-derived-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-derived-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-derived-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-derived-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-derived-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-derived-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        userConfigQueryPort.GetAsync("scope-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StudioUserConfig(string.Empty)));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/proxy/s/api-github/user", """{"login":"derived-user"}""");
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-derived-1","full_key":"full-key-derived-1"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(userConfigQueryPort);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-derived-1",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(InitializeSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().SkillContent.Contains("Primary GitHub username: derived-user", StringComparison.Ordinal) &&
                    e.Payload.Unpack<InitializeSkillRunnerCommand>().ExecutionPrompt.Contains("derived-user", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());

            handler.Requests.Should().Contain(x => x.Path == "/api/v1/proxy/s/api-github/user");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_ReturnsCredentialsRequired_WhenUsernameCannotBeResolved()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();
        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        userConfigQueryPort.GetAsync("scope-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StudioUserConfig(string.Empty)));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """{"tokens":[]}""");
        handler.Add(HttpMethod.Get, "/api/v1/catalog/api-github", """
            {
              "slug":"api-github",
              "provider_config_id":"provider-github",
              "provider_type":"oauth2",
              "credential_mode":"user",
              "documentation_url":"https://docs.github.com/en/apps/oauth-apps"
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/providers/provider-github/credentials", """
            {
              "provider_config_id":"provider-github",
              "has_credentials":true
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/providers/provider-github/connect/oauth", """
            {
              "authorization_url":"https://github.example.com/oauth/start"
            }
            """);

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(userConfigQueryPort);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("credentials_required");
            doc.RootElement.GetProperty("authorization_url").GetString().Should().Be("https://github.example.com/oauth/start");
            doc.RootElement.GetProperty("note").GetString().Should().Contain("run /daily again");

            await actorRuntime.DidNotReceive().CreateAsync<SkillRunnerGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_SavesGithubUsernamePreference_WhenRequested()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-save-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-save-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-save-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-save-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-save-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-save-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var userConfigCommandService = Substitute.For<IUserConfigCommandService>();

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-save-1","full_key":"full-key-save-1"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(userConfigCommandService);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-save-1",
                  "github_username": "alice",
                  "save_github_username_preference": true,
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");
            doc.RootElement.GetProperty("github_username").GetString().Should().Be("alice");
            doc.RootElement.GetProperty("github_username_preference_saved").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("run_immediately_requested").GetBoolean().Should().BeFalse();

            await userConfigCommandService.Received(1)
                .SaveGithubUsernameAsync("scope-1", "alice", Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_FailsClosed_When_RequiredProxyServices_AreMissing()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {
                  "provider_id":"provider-github",
                  "provider_name":"GitHub",
                  "provider_slug":"github",
                  "provider_type":"oauth2",
                  "status":"active",
                  "connected_at":"2026-04-15T00:00:00Z"
                }
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            // #417: when a required slug has no UserService row, surface a structured
            // `service_not_connected` error naming the slug (was: free-text "Missing required
            // Nyx proxy services" wrapped in `{error: "..."}`). The actor must NOT be created
            // and no api-key request should fire.
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("service_not_connected");
            doc.RootElement.GetProperty("slug").GetString().Should().Be("api-github");
            doc.RootElement.GetProperty("hint").GetString().Should().Contain("api-github");
            handler.Requests.Should().NotContain(x => x.Method == HttpMethod.Post && x.Path == "/api/v1/api-keys");
            await actorRuntime.DidNotReceive().CreateAsync<SkillRunnerGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_FailsClosed_When_RequiredSlug_IsInactive()
    {
        // #417: when the user has a UserService row for the required slug but it's marked
        // `is_active: false`, surface `service_inactive` rather than persisting an api-key
        // that NyxID's enforcement will reject at proxy time.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {"provider_id":"provider-github","provider_name":"GitHub","provider_slug":"github","provider_type":"oauth2","status":"active","connected_at":"2026-04-15T00:00:00Z"}
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":false,"credential_source":{"type":"personal"}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("service_inactive");
            doc.RootElement.GetProperty("slug").GetString().Should().Be("api-github");
            handler.Requests.Should().NotContain(x => x.Method == HttpMethod.Post && x.Path == "/api/v1/api-keys");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_FailsClosed_When_OrgSharedSlug_IsViewerOnly()
    {
        // #417: when the only matching UserService row is org-shared with `allowed: false`
        // (org viewer role), don't bind it as a proxy target — NyxID would reject the proxy
        // call later as `org_role_insufficient`. Surface `service_org_viewer_only` so the
        // user knows to ask an admin or connect a personal credential.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {"provider_id":"provider-github","provider_name":"GitHub","provider_slug":"github","provider_type":"oauth2","status":"active","connected_at":"2026-04-15T00:00:00Z"}
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github","slug":"api-github","is_active":true,"credential_source":{"type":"org","org_id":"org-1","role":"viewer","allowed":false}},
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("service_org_viewer_only");
            doc.RootElement.GetProperty("slug").GetString().Should().Be("api-github");
            handler.Requests.Should().NotContain(x => x.Method == HttpMethod.Post && x.Path == "/api/v1/api-keys");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_AllowedServiceIds_AreUserServiceIds_NotCatalogIds()
    {
        // #417 regression pin. The bug: backend used `GET /proxy/services` (catalog list) and
        // populated the new api-key's `allowed_service_ids` with `DownstreamService.id` (catalog
        // UUIDs). NyxID's proxy enforcement (proxy.rs:1030) compares against `UserService.id`
        // (per-user instance UUIDs). The mismatch was silently accepted on api-key create and
        // 403'd on every proxy call. The fix routes through `/user-services`, returning per-user
        // ids. Stub a response where the per-user `id` is *distinct from* `catalog_service_id`
        // and pin that the api-key payload carries the per-user `id` value.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-id-pin", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-id-pin", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-id-pin",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-id-pin");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-id-pin").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-id-pin", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {"provider_id":"provider-github","provider_name":"GitHub","provider_slug":"github","provider_type":"oauth2","status":"active","connected_at":"2026-04-15T00:00:00Z"}
              ]
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"user-svc-github-instance","slug":"api-github","catalog_service_id":"catalog-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"user-svc-lark-instance","slug":"api-lark-bot","catalog_service_id":"catalog-lark","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-id-pin","full_key":"full-key-id-pin"}""");
        handler.Add(HttpMethod.Get, "/api/v1/proxy/s/api-github/rate_limit",
            """{"resources":{"core":{"limit":5000,"remaining":4999}}}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            [ChannelMetadataKeys.SenderId] = "ou_user_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-id-pin",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");

            var apiKeyRequest = handler.Requests.Should()
                .ContainSingle(x => x.Method == HttpMethod.Post && x.Path == "/api/v1/api-keys")
                .Subject;
            using var apiKeyDoc = JsonDocument.Parse(apiKeyRequest.Body!);
            var allowed = apiKeyDoc.RootElement.GetProperty("allowed_service_ids").EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray();
            allowed.Should().BeEquivalentTo(["user-svc-github-instance", "user-svc-lark-instance"]);
            allowed.Should().NotContain("catalog-github").And.NotContain("catalog-lark");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_PicksEligibleRow_When_DuplicateSlugRowsExist()
    {
        // Codex review (PR #418 r3141846173): a user with mixed bindings can have multiple
        // UserService rows for the same slug — e.g. an org-shared `allowed:false` row and a
        // personal active row. NyxID does not guarantee any ordering, so the resolver must
        // pick the *eligible* row regardless of position. Pin the case where the ineligible
        // row arrives first; the resolver must still produce the personal id and succeed.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("skill-runner-dup", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("skill-runner-dup", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-dup",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusRunning,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-dup");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-dup").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<SkillRunnerGAgent>("skill-runner-dup", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(skillRunnerActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """
            {
              "tokens": [
                {"provider_id":"provider-github","provider_name":"GitHub","provider_slug":"github","provider_type":"oauth2","status":"active","connected_at":"2026-04-15T00:00:00Z"}
              ]
            }
            """);
        // Two rows for `api-github` (ineligible org-viewer first, eligible personal second) and
        // two rows for `api-lark-bot` (inactive first, active second). The resolver must pick
        // the eligible rows in both cases, not the first-seen ones.
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-github-org","slug":"api-github","is_active":true,"credential_source":{"type":"org","org_id":"org-1","role":"viewer","allowed":false}},
                {"id":"svc-github-personal","slug":"api-github","is_active":true,"credential_source":{"type":"personal"}},
                {"id":"svc-lark-stale","slug":"api-lark-bot","is_active":false,"credential_source":{"type":"personal"}},
                {"id":"svc-lark-active","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-dup","full_key":"full-key-dup"}""");
        handler.Add(HttpMethod.Get, "/api/v1/proxy/s/api-github/rate_limit",
            """{"resources":{"core":{"limit":5000,"remaining":4999}}}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            [ChannelMetadataKeys.SenderId] = "ou_user_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "agent_id": "skill-runner-dup",
                  "github_username": "alice",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");

            var apiKeyRequest = handler.Requests.Should()
                .ContainSingle(x => x.Method == HttpMethod.Post && x.Path == "/api/v1/api-keys")
                .Subject;
            using var apiKeyDoc = JsonDocument.Parse(apiKeyRequest.Body!);
            var allowed = apiKeyDoc.RootElement.GetProperty("allowed_service_ids").EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray();
            allowed.Should().BeEquivalentTo(["svc-github-personal", "svc-lark-active"]);
            allowed.Should().NotContain("svc-github-org").And.NotContain("svc-lark-stale");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_ReturnsOAuthRequirementBeforeCreatingAgent()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """{"tokens":[]}""");
        handler.Add(HttpMethod.Get, "/api/v1/catalog/api-github", """
            {
              "slug":"api-github",
              "provider_config_id":"provider-github",
              "provider_type":"oauth2",
              "credential_mode":"user",
              "documentation_url":"https://docs.github.com/en/apps/oauth-apps"
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/providers/provider-github/credentials", """
            {
              "provider_config_id":"provider-github",
              "has_credentials":true
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/providers/provider-github/connect/oauth", """
            {
              "authorization_url":"https://github.example.com/oauth/start"
            }
            """);

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "github_username": "alice",
                  "repositories": "aevatarAI/aevatar",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "run_immediately": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("oauth_required");
            doc.RootElement.GetProperty("provider").GetString().Should().Be("GitHub");
            doc.RootElement.GetProperty("provider_id").GetString().Should().Be("provider-github");
            doc.RootElement.GetProperty("authorization_url").GetString().Should().Be("https://github.example.com/oauth/start");

            await actorRuntime.DidNotReceive().CreateAsync<SkillRunnerGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
            handler.Requests.Should().NotContain(x => x.Method == HttpMethod.Post && x.Path == "/api/v1/api-keys");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_DailyReport_ReturnsCredentialsRequirementBeforeOAuth()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var actorRuntime = Substitute.For<IActorRuntime>();

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/providers/my-tokens", """{"tokens":[]}""");
        handler.Add(HttpMethod.Get, "/api/v1/catalog/api-github", """
            {
              "slug":"api-github",
              "provider_config_id":"provider-github",
              "provider_type":"oauth2",
              "credential_mode":"user",
              "documentation_url":"https://docs.github.com/en/apps/oauth-apps"
            }
            """);
        handler.Add(HttpMethod.Get, "/api/v1/providers/provider-github/credentials", """
            {
              "provider_config_id":"provider-github",
              "has_credentials":false
            }
            """);

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "daily_report",
                  "github_username": "alice",
                  "repositories": "aevatarAI/aevatar",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "run_immediately": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("credentials_required");
            doc.RootElement.GetProperty("provider").GetString().Should().Be("GitHub");
            doc.RootElement.GetProperty("provider_id").GetString().Should().Be("provider-github");
            doc.RootElement.GetProperty("documentation_url").GetString().Should().Be("https://docs.github.com/en/apps/oauth-apps");

            handler.Requests.Should().NotContain(x => x.Path == "/api/v1/providers/provider-github/connect/oauth");
            handler.Requests.Should().NotContain(x => x.Method == HttpMethod.Post && x.Path == "/api/v1/api-keys");
            await actorRuntime.DidNotReceive().CreateAsync<SkillRunnerGAgent>(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreateAgent_SocialMedia_UpsertsWorkflowAndInitializesWorkflowAgent()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetStateVersionAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(null), Task.FromResult<long?>(1));
        queryPort.GetAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "workflow-agent-1",
                AgentType = WorkflowAgentDefaults.AgentType,
                TemplateName = WorkflowAgentDefaults.TemplateName,
                Status = WorkflowAgentDefaults.StatusRunning,
            }));

        var workflowAgentActor = Substitute.For<IActor>();
        workflowAgentActor.Id.Returns("workflow-agent-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("workflow-agent-1").Returns(Task.FromResult<IActor?>(null));
        actorRuntime.CreateAsync<WorkflowAgentGAgent>("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IActor>(workflowAgentActor));

        var workflowCommandPort = Substitute.For<IScopeWorkflowCommandPort>();
        workflowCommandPort.UpsertAsync(Arg.Any<ScopeWorkflowUpsertRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ScopeWorkflowUpsertResult(
                new ScopeWorkflowSummary(
                    "scope-1",
                    "social-media-workflow-agent-1",
                    "Social Media Approval workflow-agent-1",
                    "service-key",
                    "social_media_workflow_agent_1",
                    "workflow-actor-1",
                    "rev-1",
                    "deploy-1",
                    "active",
                    DateTimeOffset.UtcNow),
                "rev-1",
                    "workflow-actor-prefix",
                    "workflow-actor-1")));

        var activationService = Substitute.For<IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease>>();
        activationService.EnsureAsync(Arg.Any<ProjectionScopeStartRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UserAgentCatalogMaterializationRuntimeLease(new UserAgentCatalogMaterializationContext
            {
                RootActorId = UserAgentCatalogGAgent.WellKnownId,
                ProjectionKind = UserAgentCatalogProjectionPort.ProjectionKind,
            })));
        var projectionPort = new UserAgentCatalogProjectionPort(activationService);

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/users/me", """{"user":{"id":"user-1"}}""");
        handler.Add(HttpMethod.Get, "/api/v1/user-services", """
            {
              "services": [
                {"id":"svc-lark","slug":"api-lark-bot","is_active":true,"credential_source":{"type":"personal"}}
              ]
            }
            """);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", """{"id":"key-2","full_key":"full-key-2"}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(workflowCommandPort);
        services.AddSingleton(nyxClient);
        services.AddSingleton(projectionPort);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.ConversationId] = "oc_chat_1",
            [ChannelMetadataKeys.SenderId] = "ou_user_1",
            ["scope_id"] = "scope-1",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create_agent",
                  "template": "social_media",
                  "agent_id": "workflow-agent-1",
                  "topic": "Launch update for the new workflow feature",
                  "audience": "Developers",
                  "style": "Confident and concise",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "run_immediately": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("created");
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("workflow-agent-1");
            doc.RootElement.GetProperty("agent_type").GetString().Should().Be(WorkflowAgentDefaults.AgentType);
            doc.RootElement.GetProperty("workflow_id").GetString().Should().Be("social-media-workflow-agent-1");
            doc.RootElement.GetProperty("api_key_id").GetString().Should().Be("key-2");

            await workflowCommandPort.Received(1).UpsertAsync(
                Arg.Is<ScopeWorkflowUpsertRequest>(request =>
                    request.ScopeId == "scope-1" &&
                    request.WorkflowId == "social-media-workflow-agent-1" &&
                    request.WorkflowYaml.Contains("provider: nyxid", StringComparison.Ordinal) &&
                    request.WorkflowYaml.Contains("type: human_approval", StringComparison.Ordinal) &&
                    request.WorkflowYaml.Contains("delivery_target_id: \"workflow-agent-1\"", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());

            await workflowAgentActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(InitializeWorkflowAgentCommand.Descriptor) &&
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().WorkflowActorId == "workflow-actor-1" &&
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().ConversationId == "oc_chat_1" &&
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().NyxApiKey == "full-key-2" &&
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().ApiKeyId == "key-2" &&
                    // Mirror of the daily_report p2p assertion: BuildFromInbound must pin the
                    // sender open_id at delivery-target creation time so FeishuCardHumanInteraction
                    // Port reads it through the catalog projection without re-deriving the type.
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().LarkReceiveId == "ou_user_1" &&
                    e.Payload.Unpack<InitializeWorkflowAgentCommand>().LarkReceiveIdType == "open_id"),
                Arg.Any<CancellationToken>());

            await workflowAgentActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(TriggerWorkflowAgentExecutionCommand.Descriptor) &&
                    e.Payload.Unpack<TriggerWorkflowAgentExecutionCommand>().Reason == "create_agent"),
                Arg.Any<CancellationToken>());

            await activationService.Received(1).EnsureAsync(
                Arg.Is<ProjectionScopeStartRequest>(request =>
                    request.RootActorId == UserAgentCatalogGAgent.WellKnownId &&
                    request.ProjectionKind == UserAgentCatalogProjectionPort.ProjectionKind),
                Arg.Any<CancellationToken>());

            var apiKeyRequest = handler.Requests.Should()
                .ContainSingle(x => x.Method == HttpMethod.Post && x.Path == "/api/v1/api-keys")
                .Subject;
            using var apiKeyDoc = JsonDocument.Parse(apiKeyRequest.Body!);
            apiKeyDoc.RootElement.GetProperty("allowed_service_ids").EnumerateArray()
                .Select(static item => item.GetString())
                .Should()
                .BeEquivalentTo(["svc-lark"]);
            apiKeyDoc.RootElement.TryGetProperty("allow_all_services", out _).Should().BeFalse();
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_DisablesActor_RevokesApiKey_AndTombstonesRegistry()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    ApiKeyId = "key-1",
                    OwnerNyxUserId = "user-1",
                }),
                Task.FromResult<UserAgentCatalogEntry?>(null));
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogEntry>>(Array.Empty<UserAgentCatalogEntry>()));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");
        var registryActor = Substitute.For<IActor>();
        registryActor.Id.Returns(UserAgentCatalogGAgent.WellKnownId);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));
        actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(registryActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-1", """{"ok":true}""");

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "delete_agent",
                  "agent_id": "skill-runner-1",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("deleted");
            doc.RootElement.GetProperty("revoked_api_key_id").GetString().Should().Be("key-1");
            doc.RootElement.GetProperty("agents").GetArrayLength().Should().Be(0);
            doc.RootElement.GetProperty("delete_notice").GetString().Should().Contain("Deleted agent");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(DisableSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<DisableSkillRunnerCommand>().Reason == "delete_agent"),
                Arg.Any<CancellationToken>());

            await registryActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(UserAgentCatalogTombstoneCommand.Descriptor) &&
                    e.Payload.Unpack<UserAgentCatalogTombstoneCommand>().AgentId == "skill-runner-1"),
                Arg.Any<CancellationToken>());

            handler.Requests.Should().ContainSingle(x =>
                x.Method == HttpMethod.Delete &&
                x.Path == "/api/v1/api-keys/key-1");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAgent_ReturnsAcceptedWithPropagatingHint_WhenTombstoneDoesNotReflectWithinBudget()
    {
        // Production bug class: with the old 5 s polling budget, /delete-agent
        // routinely returned "accepted" + "tombstone is not yet reflected" while
        // the document was still visible to /agents minutes later. This guard
        // proves that when the read model legitimately stays behind, the user-
        // facing payload now nudges the user to retry rather than implying the
        // delete might not have landed at all.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-stuck", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-stuck",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                ApiKeyId = "key-stuck",
                OwnerNyxUserId = "user-1",
            }));
        // Read-model lags forever in this test: GetStateVersionAsync keeps
        // returning the same version (the projector never advances past it),
        // and GetAsync keeps surfacing the entry.
        queryPort.GetStateVersionAsync("skill-runner-stuck", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(7L));
        queryPort.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogEntry>>(
                [new UserAgentCatalogEntry { AgentId = "skill-runner-stuck", OwnerNyxUserId = "user-1" }]));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-stuck");
        var registryActor = Substitute.For<IActor>();
        registryActor.Id.Returns(UserAgentCatalogGAgent.WellKnownId);

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-stuck").Returns(Task.FromResult<IActor?>(skillRunnerActor));
        actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId).Returns(Task.FromResult<IActor?>(registryActor));

        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-stuck", """{"ok":true}""");
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(nyxClient);
        // Inject a shrunk wait budget per-instance (3 attempts × 1 ms) so the
        // not-reflected branch fires in <100 ms instead of the production
        // 15 s. Per-instance state replaces the earlier mutable-static
        // approach (codex review r3141706856) so concurrent test classes
        // that exercise other AgentBuilderTool paths cannot be poisoned by
        // shrunk values leaking through process-global state.
        var tool = new AgentBuilderTool(
            services.BuildServiceProvider(),
            projectionWaitAttempts: 3,
            projectionWaitDelayMilliseconds: 1);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "delete_agent",
                  "agent_id": "skill-runner-stuck",
                  "confirm": true
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("revoked_api_key_id").GetString().Should().Be("key-stuck");
            doc.RootElement.GetProperty("delete_notice").GetString()
                .Should().Contain("Delete submitted for");
            // The new copy must point users at /agents to verify rather than
            // implying the tombstone did not land.
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("propagating")
                .And.Contain("/agents");

            await registryActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(UserAgentCatalogTombstoneCommand.Descriptor) &&
                    e.Payload.Unpack<UserAgentCatalogTombstoneCommand>().AgentId == "skill-runner-stuck"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_DispatchesManualTrigger()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "run_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("skill-runner-1");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(TriggerSkillRunnerExecutionCommand.Descriptor) &&
                    e.Payload.Unpack<TriggerSkillRunnerExecutionCommand>().Reason == "run_agent"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_RejectsDisabledAgent()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "skill-runner-1",
                AgentType = SkillRunnerDefaults.AgentType,
                TemplateName = "daily_report",
                Status = SkillRunnerDefaults.StatusDisabled,
            }));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "run_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            result.Should().Contain("is disabled");
            await skillRunnerActor.DidNotReceive().HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunAgent_DispatchesWorkflowTrigger()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
            {
                AgentId = "workflow-agent-1",
                AgentType = WorkflowAgentDefaults.AgentType,
                TemplateName = WorkflowAgentDefaults.TemplateName,
                Status = WorkflowAgentDefaults.StatusRunning,
            }));

        var workflowAgentActor = Substitute.For<IActor>();
        workflowAgentActor.Id.Returns("workflow-agent-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("workflow-agent-1").Returns(Task.FromResult<IActor?>(workflowAgentActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "run_agent",
                  "agent_id": "workflow-agent-1",
                  "revision_feedback": "Need stronger hook"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("agent_id").GetString().Should().Be("workflow-agent-1");
            doc.RootElement.GetProperty("note").GetString().Should().Contain("revision feedback");

            await workflowAgentActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(TriggerWorkflowAgentExecutionCommand.Descriptor) &&
                    e.Payload.Unpack<TriggerWorkflowAgentExecutionCommand>().Reason == "run_agent" &&
                    e.Payload.Unpack<TriggerWorkflowAgentExecutionCommand>().RevisionFeedback == "Need stronger hook"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_ReturnsStatusFast_WhenProjectionAdvancesOnFirstPoll()
    {
        // Pins the new version+status dual-gate fast-exit contract: when the
        // caller-captured baseline is X and the read model advances to X+1
        // with status==expected on the very first post-dispatch poll, the
        // wait helper must exit immediately (<1 s) instead of running the
        // full 15 s budget. This guards against two regressions:
        //
        //  1. Re-introducing a status-only check (codex P3 in this PR's
        //     thread): would accept a stale replica that already happens to
        //     hold the expected historical status, returning before the
        //     dispatch is actually materialized.
        //
        //  2. Re-introducing the *helper-side* baseline capture (codex P2 in
        //     PR #413's first review pass): would capture versionBefore
        //     after dispatch, so a fast projection that already advanced
        //     the version would make versionAfter == versionBefore on every
        //     poll and burn the full budget.
        //
        // Both regressions make this test fail (case 1 by accepting before
        // the dispatch, case 2 by deadlocking past the 1 s ceiling).
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-fast", Arg.Any<CancellationToken>())
            .Returns(
                // RequireManagedAgentAsync's existence check sees the pre-disable status.
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-fast",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusRunning,
                }),
                // Wait helper's first poll sees the materialized disable.
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-fast",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusDisabled,
                }));
        // Caller's pre-dispatch baseline read returns 42; helper's post-
        // dispatch poll sees 43 (the projection materialized the disable on
        // the very next state event). Both checks pass on the first iteration.
        queryPort.GetStateVersionAsync("skill-runner-fast", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(42L),
                Task.FromResult<long?>(43L));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-fast");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-fast").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await tool.ExecuteAsync("""
                {
                  "action": "disable_agent",
                  "agent_id": "skill-runner-fast"
                }
                """);
            stopwatch.Stop();

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusDisabled);
            // 1 s ceiling: any regression that prevents a dual-gate first-poll
            // exit would burn the full ProjectionWaitAttempts ×
            // ProjectionWaitDelayMilliseconds budget (15 s by default).
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_KeepsWaitingWhenStatusMatchesButVersionStale()
    {
        // Stale-replica defense: a read replica can surface a historically
        // expected status (e.g., a previous disable→enable→disable cycle
        // left the entry's last-projected status as Disabled in some replica)
        // while the current actor has not yet processed *this* dispatch.
        // Status-only polling would accept this replica and return prematurely
        // before the dispatch materializes. The dual gate keeps waiting
        // until version advances.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-stale", Arg.Any<CancellationToken>())
            .Returns(
                // RequireManagedAgentAsync sees the canonical Running state
                // because that is what the caller observed when issuing the
                // disable. (A different replica surfaces stale Disabled below.)
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-stale",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusRunning,
                }),
                // Helper's terminal fallback (after budget exhausts) returns
                // a stale-but-expected-looking Disabled. With status-only
                // polling the wait would have returned this entry on the
                // first iteration. With the dual gate the version stays at
                // baseline, so the version check short-circuits before the
                // status check is even reached.
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-stale",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusDisabled,
                }));
        // Caller baseline = 7; replica's view never advances past 7. Helper
        // must keep iterating; we shrink the budget so the test finishes fast.
        queryPort.GetStateVersionAsync("skill-runner-stale", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(7L));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-stale");
        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-stale").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        // Shrunk budget so the version-stale path finishes in <100 ms.
        var tool = new AgentBuilderTool(
            services.BuildServiceProvider(),
            projectionWaitAttempts: 3,
            projectionWaitDelayMilliseconds: 1);

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "disable_agent",
                  "agent_id": "skill-runner-stale"
                }
                """);

            using var doc = JsonDocument.Parse(result);

            // Path-level assertion: the helper exhausted the injected
            // 3-attempt budget instead of returning on the first status
            // match: 1 caller baseline + 3 helper iterations = 4 calls.
            // With status-only polling the helper would have returned on
            // iteration 0 without ever calling GetStateVersionAsync, so
            // total would be 1. Tightly coupled to the injected budget by
            // design — that is what pins the contract.
            await queryPort.Received(4).GetStateVersionAsync("skill-runner-stale", Arg.Any<CancellationToken>());

            // Outcome-level assertion: when the dual gate never passes, the
            // user-facing payload must NOT claim success. The wait helper
            // returns Confirmed=false (no un-gated GetAsync fallback), and
            // DisableAgentAsync surfaces the pre-dispatch entry plus an
            // honest "submitted / propagating" note. A regression that
            // re-introduces the un-gated final read OR drops the
            // confirmed/unconfirmed branching makes this test fail by
            // surfacing "Scheduling paused" + status=Disabled despite the
            // dual gate having been violated.
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusRunning);
            var note = doc.RootElement.GetProperty("note").GetString();
            note.Should().Contain("Disable submitted")
                .And.Contain("/agent-status")
                .And.NotContain("Scheduling paused");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_DispatchesDisableAndReturnsStatus()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }));
        // Caller's pre-dispatch baseline read returns 5; helper's post-dispatch
        // poll sees 6, satisfying the new version+status dual gate.
        queryPort.GetStateVersionAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(5L),
                Task.FromResult<long?>(6L));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "disable_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusDisabled);
            doc.RootElement.GetProperty("note").GetString().Should().Contain("Scheduling paused");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(DisableSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<DisableSkillRunnerCommand>().Reason == "disable_agent"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_EnableAgent_DispatchesEnableAndReturnsStatus()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "skill-runner-1",
                    AgentType = SkillRunnerDefaults.AgentType,
                    TemplateName = "daily_report",
                    Status = SkillRunnerDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }));
        // Caller's pre-dispatch baseline read returns 5; helper's post-dispatch
        // poll sees 6, satisfying the new version+status dual gate.
        queryPort.GetStateVersionAsync("skill-runner-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(5L),
                Task.FromResult<long?>(6L));

        var skillRunnerActor = Substitute.For<IActor>();
        skillRunnerActor.Id.Returns("skill-runner-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("skill-runner-1").Returns(Task.FromResult<IActor?>(skillRunnerActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "enable_agent",
                  "agent_id": "skill-runner-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(SkillRunnerDefaults.StatusRunning);
            doc.RootElement.GetProperty("note").GetString().Should().Contain("Scheduling resumed");

            await skillRunnerActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(EnableSkillRunnerCommand.Descriptor) &&
                    e.Payload.Unpack<EnableSkillRunnerCommand>().Reason == "enable_agent"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisableAgent_DispatchesWorkflowDisableAndReturnsStatus()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "workflow-agent-1",
                    AgentType = WorkflowAgentDefaults.AgentType,
                    TemplateName = WorkflowAgentDefaults.TemplateName,
                    Status = WorkflowAgentDefaults.StatusRunning,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }),
                Task.FromResult<UserAgentCatalogEntry?>(new UserAgentCatalogEntry
                {
                    AgentId = "workflow-agent-1",
                    AgentType = WorkflowAgentDefaults.AgentType,
                    TemplateName = WorkflowAgentDefaults.TemplateName,
                    Status = WorkflowAgentDefaults.StatusDisabled,
                    ScheduleCron = "0 9 * * *",
                    ScheduleTimezone = "UTC",
                }));
        // Caller's pre-dispatch baseline read returns 5; helper's post-dispatch
        // poll sees 6, satisfying the new version+status dual gate.
        queryPort.GetStateVersionAsync("workflow-agent-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<long?>(5L),
                Task.FromResult<long?>(6L));

        var workflowAgentActor = Substitute.For<IActor>();
        workflowAgentActor.Id.Returns("workflow-agent-1");

        var actorRuntime = Substitute.For<IActorRuntime>();
        actorRuntime.GetAsync("workflow-agent-1").Returns(Task.FromResult<IActor?>(workflowAgentActor));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(actorRuntime);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(new RoutingJsonHandler())
            {
                BaseAddress = new Uri("https://nyx.example.com"),
            }));
        var tool = new AgentBuilderTool(services.BuildServiceProvider());

        AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        };
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "disable_agent",
                  "agent_id": "workflow-agent-1"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be(WorkflowAgentDefaults.StatusDisabled);
            doc.RootElement.GetProperty("note").GetString().Should().Contain("Scheduling paused");

            await workflowAgentActor.Received(1).HandleEventAsync(
                Arg.Is<EventEnvelope>(e =>
                    e.Payload != null &&
                    e.Payload.Is(DisableWorkflowAgentCommand.Descriptor) &&
                    e.Payload.Unpack<DisableWorkflowAgentCommand>().Reason == "disable_agent"),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    [Fact]
    public async Task ToolSource_Always_ReturnsTool()
    {
        var source = new AgentBuilderToolSource(new ServiceCollection().BuildServiceProvider());
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("agent_builder");
    }

    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedRequest> Requests { get; } = [];

        public void Add(HttpMethod method, string path, string json)
        {
            _responses[BuildKey(method, path)] = json;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, path, body));

            if (_responses.TryGetValue(BuildKey(request.Method, path), out var json))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":true,"message":"not found"}""", Encoding.UTF8, "application/json"),
            };
        }

        private static string BuildKey(HttpMethod method, string path) => $"{method.Method}:{path}";
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);

    private sealed class StubUserConfigQueryPort : IUserConfigQueryPort
    {
        private readonly StudioUserConfig _config;

        public StubUserConfigQueryPort(StudioUserConfig config)
        {
            _config = config;
        }

        public Task<StudioUserConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(_config);

        public Task<StudioUserConfig> GetAsync(string scopeId, CancellationToken ct = default) => Task.FromResult(_config);
    }

    private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
    {
        public string? SavedScopeId { get; private set; }
        public StudioUserConfig? SavedConfig { get; private set; }
        public string? SavedGithubUsername { get; private set; }

        public Task SaveAsync(StudioUserConfig config, CancellationToken ct = default)
        {
            SavedConfig = config;
            return Task.CompletedTask;
        }

        public Task SaveAsync(string scopeId, StudioUserConfig config, CancellationToken ct = default)
        {
            SavedScopeId = scopeId;
            return SaveAsync(config, ct);
        }

        public Task SaveGithubUsernameAsync(string scopeId, string githubUsername, CancellationToken ct = default)
        {
            SavedScopeId = scopeId;
            SavedGithubUsername = githubUsername;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="ILogger{T}"/> that records each log call so tests can assert
    /// on level + formatted message. Avoids a full Microsoft.Extensions.Logging.Testing dependency
    /// for a single observability assertion.
    /// </summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        public sealed record LogEntry(LogLevel Level, string Message);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
