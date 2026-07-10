using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ScheduledAgentCreatorToolTests
{
    [Fact]
    public void ToolContract_ShouldNeverRequireApproval_AndExposeClosedSchema()
    {
        var tool = CreateHarness().Tool;

        tool.Name.Should().Be("scheduled_agent_creator");
        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        tool.IsReadOnly.Should().BeFalse();
        tool.IsDestructive.Should().BeFalse();

        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        var properties = schema.RootElement.GetProperty("properties");
        properties.TryGetProperty("scope_id", out _).Should().BeFalse();
        properties.TryGetProperty("owner_scope", out _).Should().BeFalse();
        properties.TryGetProperty("nyx_api_key", out _).Should().BeFalse();
        properties.TryGetProperty("nyx_provider_slug", out _).Should().BeFalse();
        properties.TryGetProperty("allowed_service_ids", out _).Should().BeFalse();
        properties.TryGetProperty("skill_content", out _).Should().BeFalse();
        properties.TryGetProperty("provider_base_url", out _).Should().BeFalse();
        properties.TryGetProperty("schedule_mode", out var scheduleMode).Should().BeTrue();
        scheduleMode.GetProperty("enum").EnumerateArray().Select(static x => x.GetString())
            .Should().BeEquivalentTo("cron", "one_shot");
        properties.TryGetProperty("delay_seconds", out _).Should().BeTrue();
        properties.TryGetProperty("run_at_utc", out _).Should().BeTrue();
        properties.TryGetProperty("one_shot_message", out _).Should().BeTrue();
        properties.TryGetProperty("required_service_slugs", out var requiredServiceSlugs).Should().BeTrue();
        requiredServiceSlugs.GetProperty("items").GetProperty("type").GetString().Should().Be("string");
        properties.TryGetProperty("output_format", out var outputFormat).Should().BeTrue();
        outputFormat.GetProperty("enum").EnumerateArray().Select(static x => x.GetString())
            .Should().BeEquivalentTo("auto", "text", "feishu_doc");
        properties.TryGetProperty("external_trigger_sources", out var externalSources).Should().BeTrue();
        externalSources.GetProperty("items").GetProperty("properties")
            .GetProperty("kind")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static x => x.GetString())
            .Should().BeEquivalentTo("webhook", "channel_inbound");
        schema.RootElement.GetProperty("required").EnumerateArray().Select(static x => x.GetString())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoToken_ShouldFailClosed()
    {
        var harness = CreateHarness();

        var result = await harness.Tool.ExecuteAsync(BaseArgs);

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("error").GetString().Should().Contain("access token");
        harness.Handler.Requests.Should().BeEmpty();
        await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
            Arg.Any<string>(),
            Arg.Any<InitializeSkillRunnerCommand>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerScopeUnavailable_ShouldFailClosedBeforeNyxCalls()
    {
        var harness = CreateHarness(callerScopeUnavailable: true);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("caller_scope_unavailable");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Theory]
    [InlineData("""{"skill_ref":"","schedule_cron":"0 9 * * *","schedule_timezone":"UTC"}""")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"","schedule_timezone":"UTC"}""")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 9 * * *","schedule_timezone":""}""")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 9 * * *","schedule_timezone":"UTC","nyx_api_key":"bad"}""")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 9 * * *","schedule_timezone":"UTC","required_service_slugs":"tavily-search"}""")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 9 * * *","schedule_timezone":"UTC","required_service_slugs":["tavily-search",123]}""")]
    public async Task ExecuteAsync_InvalidRequests_ShouldFailBeforeKeyCreation(string argumentsJson)
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(argumentsJson);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("validation_error");
            harness.Handler.Requests.Should().BeEmpty();
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData("""{"schedule_mode":"one_shot","one_shot_message":"Ping me"}""", "provide exactly one of delay_seconds or run_at_utc")]
    [InlineData("""{"schedule_mode":"one_shot","delay_seconds":1,"one_shot_message":"Ping me"}""", "at least 10 seconds")]
    [InlineData("""{"schedule_mode":"one_shot","delay_seconds":40000000,"one_shot_message":"Ping me"}""", "at most 366 days")]
    [InlineData("""{"schedule_mode":"one_shot","delay_seconds":60,"run_at_utc":"2099-01-01T00:00:00Z","one_shot_message":"Ping me"}""", "exactly one")]
    [InlineData("""{"schedule_mode":"one_shot","delay_seconds":60}""", "one_shot_message is required")]
    [InlineData("""{"schedule_mode":"instant","delay_seconds":60,"one_shot_message":"Ping me"}""", "schedule_mode")]
    [InlineData("""{"schedule_mode":"one_shot","delay_seconds":60,"one_shot_message":"Ping me","run_immediately":true}""", "run_immediately is not supported")]
    public async Task ExecuteAsync_InvalidOneShotRequests_ShouldFailBeforeKeyCreation(
        string argumentsJson,
        string expectedDetailFragment)
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(argumentsJson);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("validation_error");
            document.RootElement.GetProperty("detail").GetString().Should().Contain(expectedDetailFragment);
            harness.Handler.Requests.Should().BeEmpty();
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 0 9 * * *","schedule_timezone":"UTC"}""", "invalid_schedule_cron")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"every morning","schedule_timezone":"UTC"}""", "invalid_schedule_cron")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 9 * * *","schedule_timezone":"Mars/OlympusMons"}""", "invalid_schedule_timezone")]
    public async Task ExecuteAsync_UnschedulableCronOrTimezone_ShouldFailBeforeKeyCreation(
        string argumentsJson,
        string expectedDetailPrefix)
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(argumentsJson);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("validation_error");
            document.RootElement.GetProperty("detail").GetString().Should().StartWith(expectedDetailPrefix);
            harness.Handler.Requests.Should().BeEmpty();
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_FiveFieldCronWithNamedDays_ShouldStayAccepted()
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "skill_ref": "daily-report",
                  "schedule_cron": "*/15 9-18 * * MON-FRI",
                  "schedule_timezone": "Asia/Singapore"
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        });
    }

    [Fact]
    public async Task ExecuteAsync_InvalidOutputFormat_ShouldFailBeforeKeyCreation()
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "skill_ref": "daily",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "output_format": "pdf"
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("validation_error");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("output_format");
            harness.Handler.Requests.Should().BeEmpty();
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData("not-json", "invalid JSON literal")]
    [InlineData("""["daily","0 9 * * *","UTC"]""", "arguments must be a JSON object")]
    public async Task ExecuteAsync_WhenArgumentsMalformed_ShouldFailBeforeKeyCreation(
        string argumentsJson,
        string expectedErrorFragment)
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(argumentsJson);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Contain(expectedErrorFragment);
            harness.Handler.Requests.Should().BeEmpty();
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_VersionedSkillRef_ShouldReturnTypedErrorBeforeSideEffects()
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "skill_ref": "daily@1.2",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("versioned_skill_ref_not_supported_yet");
            document.RootElement.GetProperty("skill_ref").GetString().Should().Be("daily@1.2");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenTrustedOutboundSlugMissing_ShouldFailClosedBeforeKeyCreation()
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            AgentToolRequestContext.Current = AgentToolRequestContext.Current! with
            {
                ExternalMetadata = BaseExternalMetadata(includeOutboundSlug: false),
            };

            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("validation_error");
            document.RootElement.GetProperty("detail").GetString().Should().Be("lark_outbound_provider_slug_unavailable");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Theory]
    [InlineData("missing_scope", "scope_id_unavailable")]
    [InlineData("missing_conversation", "conversation_id_unavailable")]
    [InlineData("missing_receive_target", "lark_receive_target_unavailable")]
    public async Task ExecuteAsync_WhenTrustedContextIncomplete_ShouldFailClosedBeforeKeyCreation(
        string caseName,
        string expectedDetail)
    {
        var harness = CreateHarness();

        await WithToolContext(CreateContextForValidationCase(caseName), async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("validation_error");
            document.RootElement.GetProperty("detail").GetString().Should().Be(expectedDetail);
            harness.Handler.Requests.Should().BeEmpty();
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData("""{"error":true,"message":"service lookup denied"}""", "service_resolution_failed")]
    [InlineData("not-json", "service_resolution_invalid_json")]
    public async Task ExecuteAsync_WhenServiceKeyListResponseInvalid_ShouldFailClosedWithoutKeyCreation(
        string servicesResponseJson,
        string expectedError)
    {
        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/keys", servicesResponseJson);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
            handler.Requests.Should().ContainSingle(request => request.Method == HttpMethod.Get);
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Delete);
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequiredServiceMissing_ShouldFailClosedWithoutBroadKey()
    {
        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/keys", """{"keys":[{"id":"svc-lark","slug":"api-lark-bot"}]}""");
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("required_service_not_found:ornn-api");
            handler.Requests.Should().ContainSingle(request => request.Method == HttpMethod.Get);
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequiredServiceAmbiguous_ShouldFailClosedWithoutKeyCreation()
    {
        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/keys", """
            {
              "keys": [
                {"id":"svc-ornn-1","slug":"ornn-api"},
                {"id":"svc-ornn-2","slug":"ornn-api"},
                {"id":"svc-lark","slug":"api-lark-bot"}
              ]
            }
            """);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("required_service_ambiguous:ornn-api");
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
        });
    }

    [Fact]
    public async Task ExecuteAsync_RequiredServiceSlugs_ShouldBeResolvedIntoScopedKeyAllowlist()
    {
        var handler = CreateSuccessHandler();
        handler.Add(HttpMethod.Get, "/api/v1/keys", """
            {
              "keys": [
                {"id":"svc-ornn","slug":"ornn-api"},
                {"id":"svc-lark","slug":"api-lark-bot"},
                {"id":"svc-lark-failure","slug":"api-lark-bot-inbound"},
                {"id":"svc-tavily","slug":"tavily-search"},
                {"id":"svc-github","slug":"api-github"}
              ]
            }
            """);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "skill_ref": "daily-report",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "required_service_slugs": [" tavily-search ", "api-github", "api-github", "api-lark-bot"]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            var createRequest = handler.Requests.Single(request => request.Method == HttpMethod.Post);
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-ornn", "svc-lark", "svc-lark-failure", "svc-tavily", "svc-github");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenOwnerPinsCustomLlmProxyRoute_ShouldGrantScopedKeyAccessToThatService()
    {
        // Regression for the scheduled-run 403 incident: the bot owner pre-configured a custom
        // NyxID LLM route (`/api/v1/proxy/s/chrono-llm`, model gpt-5.5). The scoped key was minted
        // with allow_all_services=false but its allowlist omitted chrono-llm, so the schedule fired
        // yet every run failed NyxID's proxy scope check with HTTP 403 api_key_scope_forbidden.
        // The issued key must be authorized for the owner's pinned LLM route.
        var handler = CreateSuccessHandler();
        handler.Add(HttpMethod.Get, "/api/v1/keys", """
            {
              "keys": [
                {"id":"svc-ornn","slug":"ornn-api"},
                {"id":"svc-lark","slug":"api-lark-bot"},
                {"id":"svc-lark-failure","slug":"api-lark-bot-inbound"},
                {"id":"svc-chrono","slug":"chrono-llm"}
              ]
            }
            """);

        var ownerLlmConfigSource = Substitute.For<IOwnerLlmConfigSource>();
        ownerLlmConfigSource.GetForScopeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OwnerLlmConfig("gpt-5.5", "/api/v1/proxy/s/chrono-llm", 0)));

        var harness = CreateHarness(handler: handler, ownerLlmConfigSource: ownerLlmConfigSource);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            await ownerLlmConfigSource.Received().GetForScopeAsync("scope-bot-1", Arg.Any<CancellationToken>());

            var createRequest = handler.Requests.Single(request => request.Method == HttpMethod.Post);
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-ornn", "svc-lark", "svc-lark-failure", "svc-chrono");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenOwnerUsesGatewayRoute_ShouldNotWidenScopedKeyAllowlist()
    {
        // The shared gateway route uses the bearer token directly and needs no per-service grant,
        // so a gateway/empty PreferredLlmRoute must leave the scoped allowlist unchanged.
        var handler = CreateSuccessHandler();
        var ownerLlmConfigSource = Substitute.For<IOwnerLlmConfigSource>();
        ownerLlmConfigSource.GetForScopeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OwnerLlmConfig("gpt-5.5", null, 0)));

        var harness = CreateHarness(handler: handler, ownerLlmConfigSource: ownerLlmConfigSource);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            var createRequest = handler.Requests.Single(request => request.Method == HttpMethod.Post);
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-ornn", "svc-lark", "svc-lark-failure");
        });
    }

    [Theory]
    [InlineData("/api/v1/proxy/s/chrono-llm", "chrono-llm")]
    [InlineData("/api/v1/proxy/s/chrono-llm/v1", "chrono-llm")]
    [InlineData("  /api/v1/proxy/s/Custom-LLM  ", "Custom-LLM")]
    [InlineData("chrono-llm", "chrono-llm")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("/api/v1/llm/gateway/v1", null)]
    [InlineData("https://nyx.example.com/api/v1/proxy/s/chrono-llm", null)]
    public void ExtractProxyServiceSlug_ShouldReturnSlugOnlyForProxyServiceRoutes(string? route, string? expected)
    {
        ScheduledAgentApiKeyIssuer.ExtractProxyServiceSlug(route).Should().Be(expected);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDeclaredRuntimeServiceMissing_ShouldFailBeforeKeyCreation()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "skill_ref": "daily-report",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "required_service_slugs": ["tavily-search"]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("required_service_not_found:tavily-search");
            handler.Requests.Should().ContainSingle(request => request.Method == HttpMethod.Get);
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData("""{"error":true,"message":"create failed"}""", "api_key_create_failed")]
    [InlineData("not-json", "api_key_create_invalid_json")]
    [InlineData("""{"full_key":"full-secret-key"}""", "api_key_create_missing_id")]
    [InlineData("""{"id":"key-created"}""", "api_key_create_missing_full_key")]
    public async Task ExecuteAsync_WhenApiKeyCreateResponseInvalid_ShouldFailWithoutDispatch(
        string createResponseJson,
        string expectedError)
    {
        var handler = CreateSuccessHandler(createResponseJson);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be(expectedError);
            handler.Requests.Should().ContainSingle(request => request.Method == HttpMethod.Get);
            handler.Requests.Should().ContainSingle(request => request.Method == HttpMethod.Post);
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Delete);
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenApiKeyCreateReturns400_ShouldSurfaceNyxIdReasonAndHttpStatus()
    {
        // Regression for the 2026-06-15 Lark incident: a personal-owned key referencing org-owned
        // services makes NyxID reject create with HTTP 400 ("UserService '<id>' not found or not
        // owned by user"). The reason must reach the chat/runner instead of an opaque code.
        var handler = CreateSuccessHandler();
        handler.Add(
            HttpMethod.Post,
            "/api/v1/api-keys",
            """{"error":"validation_error","message":"UserService 'svc-lark' not found or not owned by user"}""",
            HttpStatusCode.BadRequest);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("api_key_create_failed");
            document.RootElement.GetProperty("http_status").GetInt32().Should().Be(400);
            document.RootElement.GetProperty("detail").GetString().Should().Contain("not owned by user");
            document.RootElement.GetProperty("hint").GetString().Should().Contain("organization");
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Delete);
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequiredServicesAreOrgOwned_ShouldMintKeyUnderTargetOrg()
    {
        // Every required service is shared through the same org -> the scoped key must be created
        // under that org's user_id so NyxID's per-owner allowed_service_ids check passes.
        var handler = CreateSuccessHandler(serviceListJson: """
            {
              "keys": [
                {"id":"svc-ornn","slug":"ornn-api","credential_source":{"type":"org","org_id":"org-chrono","org_name":"ChronoAI"}},
                {"id":"svc-lark","slug":"api-lark-bot","credential_source":{"type":"org","org_id":"org-chrono","org_name":"ChronoAI"}},
                {"id":"svc-lark-failure","slug":"api-lark-bot-inbound","credential_source":{"type":"org","org_id":"org-chrono","org_name":"ChronoAI"}}
              ]
            }
            """);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            var createRequest = handler.Requests.Single(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("target_org_id").GetString().Should().Be("org-chrono");
            createBody.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray()
                .Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-ornn", "svc-lark", "svc-lark-failure");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequiredServicesSpanOwners_ShouldFailClosed()
    {
        var handler = CreateSuccessHandler(serviceListJson: """
            {
              "keys": [
                {"id":"svc-ornn","slug":"ornn-api"},
                {"id":"svc-lark","slug":"api-lark-bot","credential_source":{"type":"org","org_id":"org-chrono","org_name":"ChronoAI","allowed":true}},
                {"id":"svc-lark-failure","slug":"api-lark-bot-inbound"}
              ]
            }
            """);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("required_services_cross_owner");
            handler.Requests.Should().NotContain(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenApiKeyLifetimeInvalid_ShouldFailBeforeKeyCreation()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(
            handler: handler,
            options: new ScheduledAgentCreatorOptions { ApiKeyLifetimeDays = 0 });

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("api_key_lifetime_invalid");
            handler.Requests.Should().ContainSingle(request => request.Method == HttpMethod.Get);
            handler.Requests.Should().NotContain(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequiredOrgServiceIsViewerOnly_ShouldFailUnreachableWithoutCreatingKey()
    {
        // A personal caller can mint an allow-all key spanning owners, but only for org services it
        // can actually proxy. An org service the caller merely views (allowed=false) would 403 on
        // every run, so refuse up front instead of minting a doomed key.
        var handler = CreateSuccessHandler(serviceListJson: """
            {
              "keys": [
                {"id":"svc-ornn","slug":"ornn-api"},
                {"id":"svc-lark","slug":"api-lark-bot","credential_source":{"type":"org","org_id":"org-chrono","org_name":"ChronoAI","allowed":false}},
                {"id":"svc-lark-failure","slug":"api-lark-bot-inbound"}
              ]
            }
            """);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("required_service_unreachable");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("api-lark-bot");
            document.RootElement.GetProperty("hint").GetString().Should().Contain("member");
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_Success_ShouldMintScopedKey_MapCommand_AndReturnAcceptedOnly()
    {
        var caller = OwnerScope.ForChannel("nyx-user-1", "lark", "scope-bot-1", "ou_sender");
        var secretVault = new InMemorySecretVault();
        var harness = CreateHarness(scope: caller, secretVault: secretVault);
        InitializeSkillRunnerCommand? captured = null;
        string? capturedAgentId = null;
        bool? capturedRunImmediately = null;
        harness.SkillRunnerPort.InitializeAsync(
                Arg.Do<string>(value => capturedAgentId = value),
                Arg.Do<InitializeSkillRunnerCommand>(value => captured = value),
                Arg.Do<bool>(value => capturedRunImmediately = value),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "skill_ref": "daily-report",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "Asia/Singapore",
                  "display_name": "Daily Report",
                  "execution_prompt": "Send concise bullets.",
                  "provider_name": "nyxid",
                  "model": "gpt-5.1",
                  "temperature": 0.2,
                  "max_tokens": 1200,
                  "max_tool_rounds": 6,
                  "max_history_messages": 12,
                  "requires_nyxid_proxy_success": true,
                  "output_format": "feishu_doc",
                  "external_trigger_sources": [
                    {
                      "source_id": " webhook-main ",
                      "kind": "webhook",
                      "enabled": true,
                      "display_name": " Main webhook "
                    },
                    {
                      "source_id": "channel-lark",
                      "kind": "channel_inbound",
                      "enabled": false
                    }
                  ],
                  "run_immediately": true
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            document.RootElement.GetProperty("api_key_id").GetString().Should().Be("key-created");
            document.RootElement.TryGetProperty("full_key", out _).Should().BeFalse();
            document.RootElement.TryGetProperty("committed", out _).Should().BeFalse();

            capturedAgentId.Should().StartWith("skill-runner-");
            capturedRunImmediately.Should().BeTrue();
            captured.Should().NotBeNull();
            captured!.SkillName.Should().Be("daily-report");
            captured.TemplateName.Should().Be("Daily Report");
            captured.SkillContent.Should().BeEmpty();
            captured.SkillRef.Should().NotBeNull();
            captured.SkillRef.Name.Should().Be("daily-report");
            captured.SkillRef.Source.Should().Be(SkillRunnerSkillSource.Ornn);
            captured.SkillRef.Version.Should().BeEmpty();
            captured.SkillRef.AllowInlineFallback.Should().BeFalse();
            captured.ExecutionPrompt.Should().Be("Send concise bullets.");
            captured.ScheduleCron.Should().Be("0 9 * * *");
            captured.ScheduleTimezone.Should().Be("Asia/Singapore");
            captured.Enabled.Should().BeTrue();
            captured.ScopeId.Should().Be("scope-bot-1");
            captured.ProviderName.Should().Be("nyxid");
            captured.Model.Should().Be("gpt-5.1");
            captured.Temperature.Should().Be(0.2);
            captured.MaxTokens.Should().Be(1200);
            captured.MaxToolRounds.Should().Be(6);
            captured.MaxHistoryMessages.Should().Be(12);
            captured.RequiresNyxidProxySuccess.Should().BeTrue();
            captured.OutputFormat.Should().Be(SkillRunnerOutputFormat.FeishuDoc);
            captured.ExternalTriggerSources.Should().HaveCount(2);
            captured.ExternalTriggerSources[0].SourceId.Should().Be("webhook-main");
            captured.ExternalTriggerSources[0].Kind.Should().Be(ExternalTriggerSourceKind.Webhook);
            captured.ExternalTriggerSources[0].Enabled.Should().BeTrue();
            captured.ExternalTriggerSources[0].DisplayName.Should().Be("Main webhook");
            captured.ExternalTriggerSources[1].SourceId.Should().Be("channel-lark");
            captured.ExternalTriggerSources[1].Kind.Should().Be(ExternalTriggerSourceKind.ChannelInbound);
            captured.ExternalTriggerSources[1].Enabled.Should().BeFalse();
            captured.OutboundConfig.NyxApiKey.Should().BeEmpty();
            captured.OutboundConfig.ApiKeyId.Should().Be("key-created");
            captured.OutboundConfig.NyxApiKeyReference.Should().NotBeNull();
            captured.OutboundConfig.NyxApiKeyReference.Purpose.Should().Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
            captured.OutboundConfig.NyxApiKeyReference.ExpiresAtUnixMs.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            captured.OutboundConfig.NyxApiKeyReference.OwnerScopeKey.Should().NotBeNullOrWhiteSpace();
            captured.OutboundConfig.NyxApiKeyReference.Ref.Should().NotBe("full-secret-key");
            captured.OutboundConfig.NyxProviderSlug.Should().Be("api-lark-bot");
            captured.OutboundConfig.FailureNotificationProviderSlug.Should().Be("api-lark-bot-inbound");
            captured.OutboundConfig.ConversationId.Should().Be("oc_conversation");
            captured.OutboundConfig.LarkReceiveId.Should().Be("oc_chat");
            captured.OutboundConfig.LarkReceiveIdType.Should().Be("chat_id");
            captured.OutboundConfig.LarkReceiveIdFallback.Should().Be("on_union");
            captured.OutboundConfig.LarkReceiveIdTypeFallback.Should().Be("union_id");
            captured.OutboundConfig.OutputFormat.Should().Be(SkillRunnerOutputFormat.FeishuDoc);
            captured.OutboundConfig.OwnerScope.MatchesStrictly(caller).Should().BeTrue();
            captured.OutboundConfig.OwnerNyxUserId.Should().BeEmpty();
            captured.OutboundConfig.Platform.Should().BeEmpty();

            var createRequest = harness.Handler.Requests.Single(request => request.Method == HttpMethod.Post);
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
            createBody.RootElement.GetProperty("allow_all_nodes").GetBoolean().Should().BeFalse();
            createBody.RootElement.GetProperty("scopes").GetString().Should().Be("read proxy");
            createBody.RootElement.GetProperty("expires_at").GetString().Should().NotBeNullOrWhiteSpace();
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-ornn", "svc-lark", "svc-lark-failure");
            // Personal-owned services: target_org_id is omitted so the request stays byte-identical
            // to the pre-org behavior.
            createBody.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();

            var preflight = harness.Handler.Requests.Should().ContainSingle(request =>
                    request.Method == HttpMethod.Get &&
                    request.Path == "/api/v1/proxy/s/ornn-api/api/v1/skills/daily-report/json")
                .Subject;
            preflight.Authorization.Should().NotBeNull();
            preflight.Authorization!.Scheme.Should().Be("Bearer");
            preflight.Authorization.Parameter.Should().Be("full-secret-key");

            var resolved = await secretVault.ResolveAsync(new ResolveSecretRequest(
                captured.OutboundConfig.NyxApiKeyReference.Ref,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                captured.OutboundConfig.NyxApiKeyReference.OwnerScopeKey,
                "key-created",
                "scheduled-agent-creator-test"));
            resolved.Secret.Should().Be("full-secret-key");
        });
    }

    [Fact]
    public async Task ExecuteAsync_OneShotWithExplicitNullRunAt_ShouldTreatNullAsUnsetAndSucceed()
    {
        // Regression for the 2026-06-12 group-chat incident: gpt-5.5 emitted the full schema
        // with the unused field nulled (delay_seconds=180 alongside run_at_utc=null). The
        // "exactly one of delay_seconds or run_at_utc" guard counted the present-but-null key
        // as provided and rejected every reminder. A JSON null must be treated as unset.
        var harness = CreateHarness();
        InitializeSkillRunnerCommand? capturedNullRunAt = null;
        harness.SkillRunnerPort.InitializeAsync(
                Arg.Any<string>(),
                Arg.Do<InitializeSkillRunnerCommand>(value => capturedNullRunAt = value),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "schedule_mode": "one_shot",
                  "delay_seconds": 180,
                  "run_at_utc": null,
                  "one_shot_message": "Send my daily report"
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            capturedNullRunAt.Should().NotBeNull();
            capturedNullRunAt!.ScheduleMode.Should().Be(SkillRunnerScheduleMode.OneShot);
            capturedNullRunAt.OneShotRunAt.Should().NotBeNull();
            capturedNullRunAt.OneShotMessage.Should().Be("Send my daily report");
        });
    }

    [Fact]
    public async Task ExecuteAsync_OneShotWithBlankRunAt_ShouldTreatBlankAsUnsetAndSucceed()
    {
        var harness = CreateHarness();
        InitializeSkillRunnerCommand? capturedBlankRunAt = null;
        harness.SkillRunnerPort.InitializeAsync(
                Arg.Any<string>(),
                Arg.Do<InitializeSkillRunnerCommand>(value => capturedBlankRunAt = value),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "schedule_mode": "one_shot",
                  "delay_seconds": 180,
                  "run_at_utc": "",
                  "one_shot_message": "Remind me to join the meeting"
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            capturedBlankRunAt.Should().NotBeNull();
            capturedBlankRunAt!.ScheduleMode.Should().Be(SkillRunnerScheduleMode.OneShot);
            capturedBlankRunAt.OneShotRunAt.Should().NotBeNull();
            capturedBlankRunAt.OneShotMessage.Should().Be("Remind me to join the meeting");
        });
    }

    [Fact]
    public async Task ExecuteAsync_OneShotReminder_ShouldMintLarkScopedKeyWithoutOrnnPreflight()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(handler: handler);
        InitializeSkillRunnerCommand? captured = null;
        harness.SkillRunnerPort.InitializeAsync(
                Arg.Any<string>(),
                Arg.Do<InitializeSkillRunnerCommand>(value => captured = value),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "schedule_mode": "one_shot",
                  "delay_seconds": 120,
                  "one_shot_message": "Submit the report",
                  "display_name": "Report reminder"
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            captured.Should().NotBeNull();
            captured!.ScheduleMode.Should().Be(SkillRunnerScheduleMode.OneShot);
            captured.SkillName.Should().BeEmpty();
            captured.TemplateName.Should().Be("Report reminder");
            captured.SkillRef.Should().BeNull();
            captured.SkillContent.Should().BeEmpty();
            captured.OneShotMessage.Should().Be("Submit the report");
            captured.OneShotRunAt.Should().NotBeNull();
            captured.ScheduleCron.Should().BeEmpty();
            captured.ScheduleTimezone.Should().Be(SkillRunnerDefaults.DefaultTimezone);
            captured.ExecutionPrompt.Should().Be("Send the configured one-shot reminder message exactly as written.");

            handler.Requests.Should().NotContain(request =>
                request.Method == HttpMethod.Get &&
                request.Path.Contains("/proxy/s/ornn-api/", StringComparison.Ordinal));
            var createRequest = handler.Requests.Single(request => request.Method == HttpMethod.Post);
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-lark", "svc-lark-failure");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenMintedKeyPreflightForbidden_ShouldReturnActionableErrorAndRevokeKey()
    {
        var handler = CreateSuccessHandler();
        handler.Add(
            HttpMethod.Get,
            "/api/v1/proxy/s/ornn-api/api/v1/skills/daily-report/json",
            """{"error":"forbidden","message":"API key does not have access to this service"}""",
            HttpStatusCode.Forbidden);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("scheduled_skill_preflight_access_denied");
            document.RootElement.GetProperty("http_status").GetInt32().Should().Be(403);
            document.RootElement.GetProperty("service_slug").GetString().Should().Be("ornn-api");
            document.RootElement.GetProperty("skill_ref").GetString().Should().Be("daily-report");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("missing proxy scope or service authorization");
            document.RootElement.GetProperty("hint").GetString().Should().Contain("recreate the scheduled agent");

            handler.Requests.Should().Contain(request =>
                request.Method == HttpMethod.Delete &&
                request.Path == "/api/v1/api-keys/key-created");
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenMintedKeyPreflightNotFound_ShouldReturnNotFoundAndRevokeKey()
    {
        var handler = CreateSuccessHandler();
        handler.Add(
            HttpMethod.Get,
            "/api/v1/proxy/s/ornn-api/api/v1/skills/daily-report/json",
            """{"error":"missing"}""",
            HttpStatusCode.NotFound);
        var harness = CreateHarness(handler: handler);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("scheduled_skill_preflight_skill_not_found");
            document.RootElement.GetProperty("http_status").GetInt32().Should().Be(404);
            document.RootElement.GetProperty("hint").GetString().Should().Contain("Check skill_ref");
            handler.Requests.Should().Contain(request =>
                request.Method == HttpMethod.Delete &&
                request.Path == "/api/v1/api-keys/key-created");
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenInitializeFails_ShouldBestEffortDeleteIssuedKey()
    {
        var harness = CreateHarness();
        harness.SkillRunnerPort.InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("dispatch failed"));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("initialize_failed");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("dispatch failed");
            harness.Handler.Requests.Should().Contain(request =>
                request.Method == HttpMethod.Delete &&
                request.Path == "/api/v1/api-keys/key-created");
        });
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotQueryCatalogOrPollReadModels()
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            _ = await harness.Tool.ExecuteAsync(BaseArgs);

            await harness.CatalogQueryPort.DidNotReceive().QueryByCallerAsync(
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await harness.CatalogQueryPort.DidNotReceive().GetForCallerAsync(
                Arg.Any<string>(),
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
            await harness.CatalogQueryPort.DidNotReceive().GetStateVersionForCallerAsync(
                Arg.Any<string>(),
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
        });
    }

    private const string BaseArgs = """
        {
          "skill_ref": "daily-report",
          "schedule_cron": "0 9 * * *",
          "schedule_timezone": "UTC"
        }
        """;

    private static CreatorHarness CreateHarness(
        RoutingJsonHandler? handler = null,
        OwnerScope? scope = null,
        bool callerScopeUnavailable = false,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        ISecretVault? secretVault = null,
        ScheduledAgentCreatorOptions? options = null)
    {
        handler ??= CreateSuccessHandler();

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var nyxClientFactory = new TestNyxIdApiClientFactory(nyxClient);
        var skillRunnerPort = Substitute.For<ISkillRunnerCommandPort>();
        skillRunnerPort.InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var resolver = Substitute.For<ICallerScopeResolver>();
        var resolvedScope = callerScopeUnavailable
            ? null
            : scope ?? OwnerScope.ForChannel("nyx-user-1", "lark", "scope-bot-1", "ou_sender");
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(resolvedScope));

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();

        var services = new ServiceCollection();
        services.AddSingleton<INyxIdApiClientFactory>(nyxClientFactory);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(resolver);
        services.AddSingleton(queryPort);
        services.AddSingleton(options ?? new ScheduledAgentCreatorOptions());
        services.AddSingleton<ScheduledAgentCreateRequestMapper>();
        services.AddSingleton(secretVault ?? new InMemorySecretVault());
        if (ownerLlmConfigSource is not null)
            services.AddSingleton(ownerLlmConfigSource);
        services.AddSingleton<ScheduledAgentApiKeyIssuer>();

        var provider = services.BuildServiceProvider();
        var tool = new ScheduledAgentCreatorTool(
            provider.GetRequiredService<ISkillRunnerCommandPort>(),
            provider.GetRequiredService<ICallerScopeResolver>(),
            provider.GetRequiredService<ScheduledAgentCreateRequestMapper>(),
            provider.GetRequiredService<ScheduledAgentApiKeyIssuer>());

        return new CreatorHarness(tool, handler, skillRunnerPort, queryPort);
    }

    private const string DefaultServiceListJson = """
        {
          "keys": [
            {"id":"svc-ornn","slug":"ornn-api"},
            {"id":"svc-lark","slug":"api-lark-bot"},
            {"id":"svc-lark-failure","slug":"api-lark-bot-inbound"}
          ]
        }
        """;

    private static RoutingJsonHandler CreateSuccessHandler(
        string createApiKeyResponse = """{"id":"key-created","full_key":"full-secret-key"}""",
        string? serviceListJson = null)
    {
        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Get, "/api/v1/keys", serviceListJson ?? DefaultServiceListJson);
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", createApiKeyResponse);
        handler.Add(HttpMethod.Get, "/api/v1/proxy/s/ornn-api/api/v1/skills/daily-report/json", """
            {
              "data": {
                "name": "daily-report",
                "files": { "SKILL.md": "Run daily report." }
              }
            }
            """);
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-created", """{"ok":true}""");
        return handler;
    }

    private static async Task WithToolContext(Func<Task> action) =>
        await WithToolContext(CreateToolContext(), action);

    private static async Task WithToolContext(AgentToolExecutionContext context, Func<Task> action)
    {
        AgentToolRequestContext.Current = context;
        try
        {
            await action();
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    private static AgentToolExecutionContext CreateContextForValidationCase(string caseName) =>
        caseName switch
        {
            "missing_scope" => CreateToolContext(callerScopeId: null, channelRegistrationScopeId: null),
            "missing_conversation" => CreateToolContext(
                externalMetadata: BaseExternalMetadata(includeConversationId: false)),
            "missing_receive_target" => CreateToolContext(
                channelSenderId: null,
                externalMetadata: BaseExternalMetadata(includeChatId: false, includeUnionId: false)),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null),
        };

    private static AgentToolExecutionContext CreateToolContext(
        string? callerScopeId = "scope-bot-1",
        string? channelRegistrationScopeId = "scope-bot-1",
        string? channelSenderId = "ou_sender",
        IReadOnlyDictionary<string, string>? externalMetadata = null) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("session-token", "session-token", null),
            Caller = new AgentToolCallerContext(callerScopeId, null, "message-1"),
            Channel = new AgentToolChannelContext("lark", channelSenderId, channelRegistrationScopeId, "message-1", "om_1"),
            ExternalMetadata = externalMetadata ?? BaseExternalMetadata(),
        };

    private static IReadOnlyDictionary<string, string> BaseExternalMetadata(
        bool includeOutboundSlug = true,
        bool includeConversationId = true,
        bool includeChatId = true,
        bool includeUnionId = true)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChannelMetadataKeys.ChatType] = "p2p",
            [ChannelMetadataKeys.InboundChannelBotProxySlug] = "api-lark-bot-inbound",
        };
        if (includeConversationId)
            metadata[ChannelMetadataKeys.ConversationId] = "oc_conversation";
        if (includeChatId)
            metadata[ChannelMetadataKeys.LarkChatId] = "oc_chat";
        if (includeUnionId)
            metadata[ChannelMetadataKeys.LarkUnionId] = "on_union";
        if (includeOutboundSlug)
            metadata[ChannelMetadataKeys.LarkOutboundProxySlug] = "api-lark-bot";
        return metadata;
    }

    private sealed record CreatorHarness(
        ScheduledAgentCreatorTool Tool,
        RoutingJsonHandler Handler,
        ISkillRunnerCommandPort SkillRunnerPort,
        IUserAgentCatalogQueryPort CatalogQueryPort);

    private sealed class TestNyxIdApiClientFactory : INyxIdApiClientFactory
    {
        private readonly NyxIdApiClient _client;

        public TestNyxIdApiClientFactory(NyxIdApiClient client)
        {
            _client = client;
        }

        public NyxIdApiClient CreateClient() => _client;
    }

    private sealed class RoutingJsonHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Json, HttpStatusCode Status)> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedRequest> Requests { get; } = [];

        public void Add(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _responses[$"{method.Method}:{path}"] = (json, status);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, path, body, request.Headers.Authorization));

            return _responses.TryGetValue($"{request.Method.Method}:{path}", out var response)
                ? new HttpResponseMessage(response.Status)
                {
                    Content = new StringContent(response.Json, Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"error":true,"message":"not found"}""", Encoding.UTF8, "application/json"),
                };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string? Body,
        AuthenticationHeaderValue? Authorization);
}
