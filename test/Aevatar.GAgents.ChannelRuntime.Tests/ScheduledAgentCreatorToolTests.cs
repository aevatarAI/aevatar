using Aevatar.GAgents.Scheduled;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.Workflow.Application.Abstractions.Schedules;
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
        properties.TryGetProperty("nyx_provider_slug", out var nyxProviderSlug).Should().BeTrue();
        nyxProviderSlug.GetProperty("description").GetString().Should().Contain("one-shot reminder outbound delivery provider");
        nyxProviderSlug.GetProperty("description").GetString().Should().Contain("select a connected provider");
        properties.TryGetProperty("nyx_user_service_id", out _).Should().BeTrue();
        properties.TryGetProperty("allowed_service_ids", out _).Should().BeFalse();
        properties.TryGetProperty("skill_content", out _).Should().BeFalse();
        properties.TryGetProperty("provider_base_url", out _).Should().BeFalse();
        properties.TryGetProperty("schedule_mode", out var scheduleMode).Should().BeTrue();
        scheduleMode.GetProperty("enum").EnumerateArray().Select(static x => x.GetString())
            .Should().BeEquivalentTo("cron", "one_shot");
        properties.TryGetProperty("delay_seconds", out _).Should().BeTrue();
        properties.TryGetProperty("run_at_utc", out _).Should().BeTrue();
        properties.TryGetProperty("one_shot_message", out _).Should().BeTrue();
        properties.TryGetProperty("required_service_slugs", out _).Should().BeFalse();
        properties.TryGetProperty("required_nyx_services", out var requiredNyxServices).Should().BeTrue();
        var requiredNyxServiceProperties = requiredNyxServices.GetProperty("items").GetProperty("properties");
        requiredNyxServiceProperties.TryGetProperty("user_service_id", out _).Should().BeTrue();
        requiredNyxServiceProperties.TryGetProperty("service_slug_snapshot", out _).Should().BeTrue();
        properties.TryGetProperty("output_format", out var outputFormat).Should().BeTrue();
        outputFormat.GetProperty("enum").EnumerateArray().Select(static x => x.GetString())
            .Should().BeEquivalentTo("auto", "text", "feishu_doc");
        properties.TryGetProperty("external_trigger_sources", out _).Should().BeFalse();
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
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 9 * * *","schedule_timezone":"UTC","nyx_provider_slug":"api-lark-bot-2"}""")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 9 * * *","schedule_timezone":"UTC","required_service_slugs":"tavily-search"}""")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 9 * * *","schedule_timezone":"UTC","required_service_slugs":["tavily-search",123]}""")]
    [InlineData("""{"skill_ref":"daily","schedule_cron":"0 9 * * *","schedule_timezone":"UTC","external_trigger_sources":[{"source_id":"webhook-main","kind":"webhook"}]}""")]
    public async Task ExecuteAsync_InvalidRequests_ShouldFailBeforeKeyCreation(string argumentsJson)
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(argumentsJson);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("validation_error");
            harness.Handler.Requests.Should().BeEmpty();
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
                  "schedule_timezone": "Asia/Singapore",
                  "nyx_user_service_id": "svc-lark",
                  "required_nyx_services": [
                    {"user_service_id":"svc-ornn","service_slug_snapshot":"ornn-api"},
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"}
                  ]
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
            document.RootElement.GetProperty("detail").GetString().Should().Be("channel_outbound_provider_slug_unavailable");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Theory]
    [InlineData("missing_scope", "scope_id_unavailable")]
    [InlineData("missing_conversation", "conversation_id_unavailable")]
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
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenCommittedOwnerSnapshotIsUnavailable_ShouldFailBeforeSideEffects()
    {
        var harness = CreateHarness(
            authorizationCatalogQueryPort: new FixedSnapshotQueryPort(null));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("SnapshotNotFound");
            harness.Handler.Requests.Should().BeEmpty();
            await harness.CreationPort.DidNotReceive().CreateAsync(
                Arg.Any<ScheduledWorkflowAgentCreateRequest>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenExactServiceIdentitiesAreMissing_ShouldFailBeforeKeyCreation()
    {
        var harness = CreateHarness();

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "skill_ref": "daily-report",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC"
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString()
                .Should().Be("DurableAuthorizationUnavailable");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("nyxid_exact_service_identity_unavailable");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequiredServiceMissing_ShouldFailClosedWithoutBroadKey()
    {
        var harness = CreateHarness(authorizationSnapshot: CreateSnapshot(
            ServiceEvidence("svc-ornn", "ornn-api"),
            ServiceEvidence("svc-lark", "api-lark-bot"),
            ServiceEvidence("svc-llm", "chrono-llm-public", "gpt-5.5")));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("SnapshotStale");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("nyxid_catalog_snapshot_stale");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNeverResolveRequiredServiceIdFromDuplicateSlug()
    {
        var harness = CreateHarness(authorizationSnapshot: CreateSnapshot(
            ServiceEvidence("svc-ornn-1", "ornn-api"),
            ServiceEvidence("svc-ornn-2", "ornn-api"),
            ServiceEvidence("svc-lark", "api-lark-bot"),
            ServiceEvidence("svc-lark-failure", "api-lark-bot-inbound"),
            ServiceEvidence("svc-llm", "chrono-llm-public", "gpt-5.5")));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("SnapshotStale");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("nyxid_catalog_snapshot_stale");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_ExactRequiredServices_ShouldBeCopiedIntoScopedKeyAllowlist()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(
            handler: handler,
            authorizationSnapshot: CreateSnapshot(
                DefaultAuthorizationServices
                    .Append(ServiceEvidence("svc-tavily", "tavily-search"))
                    .Append(ServiceEvidence("svc-github", "api-github"))
                    .ToArray()));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "skill_ref": "daily-report",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "nyx_user_service_id": "svc-lark",
                  "required_nyx_services": [
                    {"user_service_id":"svc-ornn","service_slug_snapshot":"ornn-api"},
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"},
                    {"user_service_id":"svc-tavily","service_slug_snapshot":"tavily-search"},
                    {"user_service_id":"svc-github","service_slug_snapshot":"api-github"},
                    {"user_service_id":"svc-github","service_slug_snapshot":"api-github"},
                    {"user_service_id":"svc-lark","service_slug_snapshot":"api-lark-bot"}
                  ]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            var createRequest = handler.Requests.Single(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().Equal(
                    "svc-github", "svc-lark", "svc-lark-failure", "svc-llm", "svc-ornn", "svc-tavily");
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Get);
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
        var ownerLLMQueryPort = new RecordingOwnerLLMEvidenceQueryPort(
            new ScheduledInvocationOwnerLLMEvidence(
                17,
                new ScheduledInvocationOwnerLLMSelection
                {
                    RouteKind = LLMRouteKind.NyxIdUserService,
                    RouteValue = "/api/v1/proxy/s/chrono-llm",
                    NyxIdUserServiceId = "svc-chrono",
                    ServiceSlugSnapshot = "chrono-llm",
                    Model = "gpt-5.5",
                }));
        var harness = CreateHarness(
            handler: handler,
            authorizationSnapshot: CreateSnapshot(
                DefaultAuthorizationServices
                    .Where(static service => service.ServiceSlug != "chrono-llm-public")
                    .Append(ServiceEvidence("svc-chrono", "chrono-llm", "gpt-5.5"))
                    .ToArray()),
            ownerLLMQueryPort: ownerLLMQueryPort);

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            ownerLLMQueryPort.ScopeIds.Should().Equal("scope-bot-1", "scope-bot-1");

            var createRequest = handler.Requests.Single(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
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
        // so an explicit typed Gateway selection must leave the scoped allowlist unchanged.
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(
            handler: handler,
            authorizationSnapshot: WithGatewayLLMTarget(
                CreateSnapshot(DefaultAuthorizationServices),
                GatewayLLMTarget("gpt-5.5")),
            ownerLLMQueryPort: new RecordingOwnerLLMEvidenceQueryPort(
                new ScheduledInvocationOwnerLLMEvidence(
                    18,
                    new ScheduledInvocationOwnerLLMSelection
                    {
                        RouteKind = LLMRouteKind.Gateway,
                        RouteValue = ScheduledInvocationOwnerLLMSelectionPolicy.GatewayRoute,
                        Model = "gpt-5.5",
                    })));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            var createRequest = handler.Requests.Single(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-ornn", "svc-lark", "svc-lark-failure");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenOwnerLlmEvidenceMissing_ShouldNotInventLlmGrant()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(
            handler: handler,
            ownerLLMQueryPort: new RecordingOwnerLLMEvidenceQueryPort(null));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            var createRequest = handler.Requests.Single(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allowed_service_ids")
                .EnumerateArray()
                .Select(static value => value.GetString())
                .Should().BeEquivalentTo("svc-ornn", "svc-lark", "svc-lark-failure");
        });
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
                  "nyx_user_service_id": "svc-lark",
                  "required_nyx_services": [
                    {"user_service_id":"svc-ornn","service_slug_snapshot":"ornn-api"},
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"},
                    {"user_service_id":"svc-tavily","service_slug_snapshot":"tavily-search"}
                  ]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("SnapshotStale");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("nyxid_catalog_snapshot_stale");
            handler.Requests.Should().BeEmpty();
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
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
            handler.Requests.Should().ContainSingle(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Delete);
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
            document.RootElement.GetProperty("hint").GetString().Should().Contain("owned");
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Delete);
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
            document.RootElement.GetProperty("error").GetString().Should().Be("authenticated_owner_context_unavailable");
            handler.Requests.Should().BeEmpty();
            handler.Requests.Should().NotContain(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequiredServiceIsViewOnly_ShouldFailBeforeCreatingKey()
    {
        var viewOnly = ServiceEvidence("svc-lark", "api-lark-bot");
        viewOnly.Access = NyxIdAuthorizationAccess.ViewOnly;
        var harness = CreateHarness(authorizationSnapshot: CreateSnapshot(
            ServiceEvidence("svc-ornn", "ornn-api"),
            viewOnly,
            ServiceEvidence("svc-lark-failure", "api-lark-bot-inbound"),
            ServiceEvidence("svc-llm", "chrono-llm-public", "gpt-5.5")));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("ServiceAccessDenied");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("nyxid_service_access_denied:svc-lark");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_Success_ShouldMintScopedKey_MapCommand_AndReturnAcceptedOnly()
    {
        var caller = OwnerScope.ForChannel("nyx-user-1", "lark", "scope-bot-1", "ou_sender");
        var secretVault = new InMemorySecretVault();
        var harness = CreateHarness(scope: caller, secretVault: secretVault);
        ScheduledWorkflowAgentCreateRequest? captured = null;
        harness.CreationPort.CreateAsync(
                Arg.Do<ScheduledWorkflowAgentCreateRequest>(value => captured = value),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduledWorkflowAgentCreateRequest>();
                return Task.FromResult(new ScheduledWorkflowAgentCreationReceipt(
                    request.Schedule.ScheduleId,
                    $"actor:{request.Schedule.ScheduleId}",
                    true,
                    "command-1",
                    "correlation-1",
                    DateTimeOffset.UtcNow,
                    "accepted"));
            });

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
                  "nyx_user_service_id": "svc-lark",
                  "required_nyx_services": [
                    {"user_service_id":"svc-ornn","service_slug_snapshot":"ornn-api"},
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"}
                  ],
                  "run_immediately": true
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            document.RootElement.GetProperty("api_key_id").GetString().Should().Be("key-created");
            document.RootElement.TryGetProperty("full_key", out _).Should().BeFalse();
            document.RootElement.TryGetProperty("committed", out _).Should().BeFalse();

            document.RootElement.GetProperty("agent_id").GetString().Should().StartWith("scheduled-workflow-");
            captured.Should().NotBeNull();
            captured!.RunImmediately.Should().BeTrue();
            captured.Schedule.ScheduleId.Should().StartWith("scheduled-workflow-");
            captured.Schedule.DisplayName.Should().Be("Daily Report");
            captured.Schedule.WorkflowName.Should().Be("daily-report");
            captured.Schedule.Prompt.Should().Be("Send concise bullets.");
            captured.Schedule.CronExpression.Should().Be("0 9 * * *");
            captured.Schedule.Timezone.Should().Be("Asia/Singapore");
            captured.Schedule.Enabled.Should().BeTrue();
            captured.Schedule.ScopeId.Should().Be("scope-bot-1");
            captured.Schedule.ScheduleMode.Should().Be(WorkflowScheduleMode.RecurringCron);
            captured.Schedule.OneShotFireAt.Should().BeNull();
            captured.Schedule.Auth.Should().NotBeNull();
            captured.Schedule.Auth!.ScheduledInvocationAgentKey.Should().NotBeNull();
            var agentKey = captured.Schedule.Auth.ScheduledInvocationAgentKey!;
            agentKey.ApiKeyId.Should().Be("key-created");
            agentKey.SecretReference.Purpose.Should().Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
            agentKey.SecretReference.ExpiresAtUnixMs.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            agentKey.SecretReference.OwnerScopeKey.Should().NotBeNullOrWhiteSpace();
            agentKey.SecretReference.Ref.Should().NotBe("full-secret-key");
            captured.Schedule.Headers["scheduled_agent.agent_type"].Should().Be(ScheduledWorkflowAgentDefaults.AgentType);
            captured.Schedule.Headers["scheduled_agent.conversation_id"].Should().Be("oc_conversation");
            captured.Schedule.Headers["scheduled_agent.output_format"].Should().Be(ScheduledAgentOutputFormat.FeishuDoc.ToString());
            captured.Schedule.Headers["scheduled_agent.api_key_id"].Should().Be("key-created");
            captured.Schedule.Headers["scheduled_agent.nyx_provider_slug"].Should().Be("api-lark-bot");
            captured.Schedule.Headers["workflow.llm.model"].Should().Be("gpt-5.1");
            captured.Schedule.Headers["workflow.llm.provider"].Should().Be("nyxid");
            captured.Schedule.Headers["workflow.llm.max_tool_rounds"].Should().Be("6");
            captured.Schedule.Headers["workflow.llm.max_history_messages"].Should().Be("12");
            captured.CatalogEntry.AgentId.Should().Be(captured.Schedule.ScheduleId);
            captured.CatalogEntry.AgentType.Should().Be(ScheduledWorkflowAgentDefaults.AgentType);
            captured.CatalogEntry.TemplateName.Should().Be("Daily Report");
            captured.CatalogEntry.ScopeId.Should().Be("scope-bot-1");
#pragma warning disable CS0612 // verifies new commands do not carry deprecated plaintext credentials
            captured.CatalogEntry.NyxApiKey.Should().BeEmpty();
#pragma warning restore CS0612
            captured.CatalogEntry.NyxApiKeyReference.Should().NotBeNull();
            captured.CatalogEntry.NyxApiKeyReference.Ref.Should().Be(agentKey.SecretReference.Ref);
            captured.CatalogEntry.NyxApiKeyReference.Purpose.Should().Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
            captured.CatalogEntry.NyxApiKeyReference.OwnerScopeKey.Should().Be(agentKey.SecretReference.OwnerScopeKey);
            captured.CatalogEntry.ApiKeyId.Should().Be("key-created");
            captured.CatalogEntry.NyxProviderSlug.Should().Be("api-lark-bot");
            captured.CatalogEntry.ConversationId.Should().Be("oc_conversation");
            captured.CatalogEntry.TargetPlatform.Should().Be("lark");
            captured.CatalogEntry.ChannelAddress.Platform.Should().Be("lark");
            captured.CatalogEntry.ChannelAddress.ProviderSlug.Should().Be("api-lark-bot");
            captured.CatalogEntry.ChannelAddress.ConversationId.Should().Be("oc_conversation");
            captured.CatalogEntry.ChannelAddress.Primary.AddressId.Should().Be("on_union");
            captured.CatalogEntry.ChannelAddress.Primary.AddressType.Should().Be("union_id");
            captured.CatalogEntry.ChannelAddress.Fallback.Should().BeNull();
#pragma warning disable CS0612 // verifies new writes leave deprecated delivery fields empty
            captured.CatalogEntry.LarkReceiveId.Should().BeEmpty();
            captured.CatalogEntry.LarkReceiveIdType.Should().BeEmpty();
            captured.CatalogEntry.LarkReceiveIdFallback.Should().BeEmpty();
            captured.CatalogEntry.LarkReceiveIdTypeFallback.Should().BeEmpty();
#pragma warning restore CS0612
            captured.CatalogEntry.OutputFormat.Should().Be(ScheduledAgentOutputFormat.FeishuDoc);
            captured.CatalogEntry.OwnerScope.MatchesStrictly(caller).Should().BeTrue();

            var createRequest = harness.Handler.Requests.Single(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
            createBody.RootElement.GetProperty("allow_all_nodes").GetBoolean().Should().BeFalse();
            createBody.RootElement.GetProperty("scopes").GetString().Should().Be("read proxy");
            createBody.RootElement.GetProperty("expires_at").GetString().Should().NotBeNullOrWhiteSpace();
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-ornn", "svc-lark", "svc-lark-failure", "svc-llm");
            // Personal-owned services: target_org_id is omitted so the request stays byte-identical
            // to the pre-org behavior.
            createBody.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();

            var resolved = await secretVault.ResolveAsync(new ResolveSecretRequest(
                agentKey.SecretReference.Ref,
                CredentialSecretPurposes.ScheduledInvocationAgentKey,
                agentKey.SecretReference.OwnerScopeKey,
                "key-created",
                "scheduled-agent-creator-test"));
            resolved.Secret.Should().Be("full-secret-key");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenInvokedFromScheduledRun_ShouldPreserveExistingOutboundSlug()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(
            handler: handler,
            authorizationSnapshot: CreateSnapshot(
                ServiceEvidence("svc-ornn", "ornn-api"),
                ServiceEvidence("svc-scheduled-lark", "api-lark-bot-scheduled"),
                ServiceEvidence("svc-lark-failure", "api-lark-bot-inbound"),
                ServiceEvidence("svc-llm", "chrono-llm-public", "gpt-5.5")));
        ScheduledWorkflowAgentCreateRequest? captured = null;
        harness.CreationPort.CreateAsync(
                Arg.Do<ScheduledWorkflowAgentCreateRequest>(value => captured = value),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduledWorkflowAgentCreateRequest>();
                return Task.FromResult(new ScheduledWorkflowAgentCreationReceipt(
                    request.Schedule.ScheduleId,
                    $"actor:{request.Schedule.ScheduleId}",
                    true,
                    "command-1",
                    "correlation-1",
                    DateTimeOffset.UtcNow,
                    "accepted"));
            });
        var metadata = new Dictionary<string, string>(BaseExternalMetadata(), StringComparer.Ordinal);
        metadata[ChannelMetadataKeys.OutboundProviderSlug] = "api-lark-bot-current";
        metadata["scheduled_agent.nyx_provider_slug"] = "api-lark-bot-scheduled";

        await WithToolContext(CreateToolContext(externalMetadata: metadata), async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "skill_ref": "daily-report",
                  "schedule_cron": "0 9 * * *",
                  "schedule_timezone": "UTC",
                  "nyx_user_service_id": "svc-scheduled-lark",
                  "required_nyx_services": [
                    {"user_service_id":"svc-ornn","service_slug_snapshot":"ornn-api"},
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"}
                  ]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            captured.Should().NotBeNull();
            captured!.Schedule.Headers["scheduled_agent.nyx_provider_slug"].Should().Be("api-lark-bot-scheduled");
            captured.CatalogEntry.NyxProviderSlug.Should().Be("api-lark-bot-scheduled");
            captured.CatalogEntry.ChannelAddress.ProviderSlug.Should().Be("api-lark-bot-scheduled");

            var createRequest = handler.Requests.Single(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-ornn", "svc-scheduled-lark", "svc-lark-failure", "svc-llm");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenDeliveryAddressTypeMissing_ShouldNotStoreChatTypeAsAddressType()
    {
        var harness = CreateHarness();
        ScheduledWorkflowAgentCreateRequest? captured = null;
        harness.CreationPort.CreateAsync(
                Arg.Do<ScheduledWorkflowAgentCreateRequest>(value => captured = value),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduledWorkflowAgentCreateRequest>();
                return Task.FromResult(new ScheduledWorkflowAgentCreationReceipt(
                    request.Schedule.ScheduleId,
                    $"actor:{request.Schedule.ScheduleId}",
                    true,
                    "command-1",
                    "correlation-1",
                    DateTimeOffset.UtcNow,
                    "accepted"));
            });

        await WithToolContext(
            CreateToolContext(
                channelDeliveryTargetId: "delivery-target-1",
                externalMetadata: BaseExternalMetadata(includeUnionId: false)),
            async () =>
            {
                var result = await harness.Tool.ExecuteAsync(BaseArgs);

                using var document = JsonDocument.Parse(result);
                document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
                captured.Should().NotBeNull();
                captured!.CatalogEntry.ChannelAddress.Primary.AddressId.Should().Be("delivery-target-1");
                captured.CatalogEntry.ChannelAddress.Primary.AddressType.Should().BeEmpty();
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
        ScheduledWorkflowAgentCreateRequest? capturedNullRunAt = null;
        harness.CreationPort.CreateAsync(
                Arg.Do<ScheduledWorkflowAgentCreateRequest>(value => capturedNullRunAt = value),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduledWorkflowAgentCreateRequest>();
                return Task.FromResult(new ScheduledWorkflowAgentCreationReceipt(
                    request.Schedule.ScheduleId,
                    $"actor:{request.Schedule.ScheduleId}",
                    true,
                    "command-1",
                    "correlation-1",
                    DateTimeOffset.UtcNow,
                    "accepted"));
            });

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "schedule_mode": "one_shot",
                  "delay_seconds": 180,
                  "run_at_utc": null,
                  "one_shot_message": "Send my daily report",
                  "nyx_user_service_id": "svc-lark",
                  "required_nyx_services": [
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"}
                  ]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            capturedNullRunAt.Should().NotBeNull();
            capturedNullRunAt!.Schedule.ScheduleMode.Should().Be(WorkflowScheduleMode.OneShotAtUtc);
            capturedNullRunAt.Schedule.OneShotFireAt.Should().NotBeNull();
            capturedNullRunAt.Schedule.Prompt.Should().Be("Send my daily report");
            capturedNullRunAt.Schedule.CronExpression.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_OneShotWithBlankRunAt_ShouldTreatBlankAsUnsetAndSucceed()
    {
        var harness = CreateHarness();
        ScheduledWorkflowAgentCreateRequest? capturedBlankRunAt = null;
        harness.CreationPort.CreateAsync(
                Arg.Do<ScheduledWorkflowAgentCreateRequest>(value => capturedBlankRunAt = value),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduledWorkflowAgentCreateRequest>();
                return Task.FromResult(new ScheduledWorkflowAgentCreationReceipt(
                    request.Schedule.ScheduleId,
                    $"actor:{request.Schedule.ScheduleId}",
                    true,
                    "command-1",
                    "correlation-1",
                    DateTimeOffset.UtcNow,
                    "accepted"));
            });

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "schedule_mode": "one_shot",
                  "delay_seconds": 180,
                  "run_at_utc": "",
                  "one_shot_message": "Remind me to join the meeting",
                  "nyx_user_service_id": "svc-lark",
                  "required_nyx_services": [
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"}
                  ]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            capturedBlankRunAt.Should().NotBeNull();
            capturedBlankRunAt!.Schedule.ScheduleMode.Should().Be(WorkflowScheduleMode.OneShotAtUtc);
            capturedBlankRunAt.Schedule.OneShotFireAt.Should().NotBeNull();
            capturedBlankRunAt.Schedule.Prompt.Should().Be("Remind me to join the meeting");
            capturedBlankRunAt.Schedule.CronExpression.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_OneShotReminder_ShouldMintLarkScopedKeyWithoutOrnnPreflight()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(handler: handler);
        ScheduledWorkflowAgentCreateRequest? captured = null;
        harness.CreationPort.CreateAsync(
                Arg.Do<ScheduledWorkflowAgentCreateRequest>(value => captured = value),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduledWorkflowAgentCreateRequest>();
                return Task.FromResult(new ScheduledWorkflowAgentCreationReceipt(
                    request.Schedule.ScheduleId,
                    $"actor:{request.Schedule.ScheduleId}",
                    true,
                    "command-1",
                    "correlation-1",
                    DateTimeOffset.UtcNow,
                    "accepted"));
            });

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "schedule_mode": "one_shot",
                  "delay_seconds": 120,
                  "one_shot_message": "Submit the report",
                  "nyx_user_service_id": "svc-lark",
                  "required_nyx_services": [
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"}
                  ],
                  "display_name": "Report reminder"
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            captured.Should().NotBeNull();
            captured!.Schedule.ScheduleMode.Should().Be(WorkflowScheduleMode.OneShotAtUtc);
            captured.Schedule.WorkflowName.Should().Be(ScheduledWorkflowAgentDefaults.DefaultWorkflowName);
            captured.Schedule.DisplayName.Should().Be("Report reminder");
            captured.Schedule.Prompt.Should().Be("Submit the report");
            captured.Schedule.OneShotFireAt.Should().NotBeNull();
            captured.Schedule.CronExpression.Should().BeEmpty();
            captured.Schedule.Timezone.Should().Be(ScheduledWorkflowAgentDefaults.DefaultTimezone);
            captured.CatalogEntry.AgentType.Should().Be(ScheduledWorkflowAgentDefaults.AgentType);

            handler.Requests.Should().NotContain(request =>
                request.Method == HttpMethod.Get &&
                request.Path.Contains("/proxy/s/ornn-api/", StringComparison.Ordinal));
            var createRequest = handler.Requests.Single(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-lark", "svc-lark-failure", "svc-llm");
        });
    }

    [Fact]
    public async Task ExecuteAsync_OneShotReminderWithExplicitNyxProviderSlug_ShouldUseSelectedConnectedProvider()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(
            handler: handler,
            authorizationSnapshot: CreateSnapshot(
                ServiceEvidence("svc-lark-2", "api-lark-bot-2"),
                ServiceEvidence("svc-lark-failure", "api-lark-bot-inbound"),
                ServiceEvidence("svc-llm", "chrono-llm-public", "gpt-5.5")));
        ScheduledWorkflowAgentCreateRequest? captured = null;
        harness.CreationPort.CreateAsync(
                Arg.Do<ScheduledWorkflowAgentCreateRequest>(value => captured = value),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduledWorkflowAgentCreateRequest>();
                return Task.FromResult(new ScheduledWorkflowAgentCreationReceipt(
                    request.Schedule.ScheduleId,
                    $"actor:{request.Schedule.ScheduleId}",
                    true,
                    "command-1",
                    "correlation-1",
                    DateTimeOffset.UtcNow,
                    "accepted"));
            });
        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "schedule_mode": "one_shot",
                  "delay_seconds": 600,
                  "one_shot_message": "Send the reminder",
                  "nyx_user_service_id": "svc-lark-2",
                  "nyx_provider_slug": "api-lark-bot-2",
                  "required_nyx_services": [
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"}
                  ]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            captured.Should().NotBeNull();
            captured!.Schedule.Headers["scheduled_agent.nyx_provider_slug"].Should().Be("api-lark-bot-2");
            captured.CatalogEntry.NyxProviderSlug.Should().Be("api-lark-bot-2");
            captured.CatalogEntry.ChannelAddress.ProviderSlug.Should().Be("api-lark-bot-2");

            var createRequest = handler.Requests.Single(request =>
                request.Method == HttpMethod.Post && request.Path == "/api/v1/api-keys");
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-lark-2", "svc-lark-failure", "svc-llm");
        });
    }

    [Fact]
    public async Task ExecuteAsync_AdditionalExactServices_ShouldNotOverrideOneShotOutboundDeliveryProvider()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(
            handler: handler,
            authorizationSnapshot: CreateSnapshot(
                ServiceEvidence("svc-lark-2", "api-lark-bot-2"),
                ServiceEvidence("svc-lark-failure", "api-lark-bot-inbound"),
                ServiceEvidence("svc-llm", "chrono-llm-public", "gpt-5.5")));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync("""
                {
                  "schedule_mode": "one_shot",
                  "delay_seconds": 600,
                  "one_shot_message": "Send the reminder",
                  "nyx_user_service_id": "svc-lark",
                  "required_nyx_services": [
                    {"user_service_id":"svc-lark-2","service_slug_snapshot":"api-lark-bot-2"},
                    {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"}
                  ]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("SnapshotStale");
            document.RootElement.GetProperty("detail").GetString()
                .Should().Be("nyxid_catalog_snapshot_stale");
            handler.Requests.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenInitializeFails_ShouldReturnInitializeFailure()
    {
        var harness = CreateHarness();
        harness.CreationPort.CreateAsync(
                Arg.Any<ScheduledWorkflowAgentCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<ScheduledWorkflowAgentCreationReceipt>>(_ => throw new InvalidOperationException("dispatch failed"));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("initialize_failed");
            document.RootElement.GetProperty("detail").GetString().Should().Contain("dispatch failed");
            await harness.CatalogCommandPort.Received(1).RequestCredentialRevocationAsync(
                Arg.Is<ScheduledAgentCredentialRevocationIntent>(intent =>
                    intent.ApiKeyId == "key-created" &&
                    intent.OwnerScope.MatchesStrictly(
                        OwnerScope.ForChannel("nyx-user-1", "lark", "scope-bot-1", "ou_sender")) &&
                    intent.NyxApiKeyReference != null &&
                    intent.VaultRevocationDescriptor.ReferenceAvailability ==
                        ScheduledCredentialVaultReferenceAvailability.Confirmed),
                Arg.Any<CancellationToken>(),
                "session-token");
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
          "schedule_timezone": "UTC",
          "nyx_user_service_id": "svc-lark",
          "required_nyx_services": [
            {"user_service_id":"svc-ornn","service_slug_snapshot":"ornn-api"},
            {"user_service_id":"svc-lark-failure","service_slug_snapshot":"api-lark-bot-inbound"}
          ]
        }
        """;

    private static CreatorHarness CreateHarness(
        RoutingJsonHandler? handler = null,
        OwnerScope? scope = null,
        bool callerScopeUnavailable = false,
        ISecretVault? secretVault = null,
        ScheduledAgentCreatorOptions? options = null,
        NyxIdAuthorizationCatalogSnapshot? authorizationSnapshot = null,
        INyxIdAuthorizationCatalogQueryPort? authorizationCatalogQueryPort = null,
        IScheduledInvocationOwnerLLMEvidenceQueryPort? ownerLLMQueryPort = null)
    {
        handler ??= CreateSuccessHandler();

        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        var nyxClientFactory = new TestNyxIdApiClientFactory(nyxClient);
        var creationPort = Substitute.For<IScheduledWorkflowAgentCreationPort>();
        creationPort.CreateAsync(
                Arg.Any<ScheduledWorkflowAgentCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<ScheduledWorkflowAgentCreateRequest>();
                return Task.FromResult(new ScheduledWorkflowAgentCreationReceipt(
                    request.Schedule.ScheduleId,
                    $"actor:{request.Schedule.ScheduleId}",
                    true,
                    "command-1",
                    "correlation-1",
                    DateTimeOffset.UtcNow,
                    "accepted"));
            });

        var resolver = Substitute.For<ICallerScopeResolver>();
        var resolvedScope = callerScopeUnavailable
            ? null
            : scope ?? OwnerScope.ForChannel("nyx-user-1", "lark", "scope-bot-1", "ou_sender");
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(resolvedScope));

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var effectiveOptions = options ?? new ScheduledAgentCreatorOptions();

        var services = new ServiceCollection();
        services.AddSingleton<INyxIdApiClientFactory>(nyxClientFactory);
        services.AddSingleton(creationPort);
        services.AddSingleton(resolver);
        services.AddSingleton(queryPort);
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(effectiveOptions);
        services.AddSingleton<ScheduledAgentCreateRequestMapper>();
        services.AddSingleton(secretVault ?? new InMemorySecretVault());
        services.AddSingleton<ScheduledAgentApiKeyIssuer>();
        services.AddSingleton<IScheduledAgentApiKeyIssuer>(sp => sp.GetRequiredService<ScheduledAgentApiKeyIssuer>());
        services.AddSingleton<ScheduledAgentCredentialLifecycle>();

        var provider = services.BuildServiceProvider();
        var planner = new ScheduledInvocationAuthorizationPlanner(
            authorizationCatalogQueryPort ?? new FixedSnapshotQueryPort(
                authorizationSnapshot ?? CreateSnapshot(DefaultAuthorizationServices)),
            ownerLLMQueryPort: ownerLLMQueryPort ?? new RecordingOwnerLLMEvidenceQueryPort(
                new ScheduledInvocationOwnerLLMEvidence(
                    29,
                    new ScheduledInvocationOwnerLLMSelection
                    {
                        RouteKind = LLMRouteKind.NyxIdUserService,
                        RouteValue = "/api/v1/proxy/s/chrono-llm-public",
                        NyxIdUserServiceId = "svc-llm",
                        ServiceSlugSnapshot = "chrono-llm-public",
                        Model = "gpt-5.5",
                    })));
        var tool = new ScheduledAgentCreatorTool(
            provider.GetRequiredService<IScheduledWorkflowAgentCreationPort>(),
            provider.GetRequiredService<ICallerScopeResolver>(),
            provider.GetRequiredService<ScheduledAgentCreateRequestMapper>(),
            provider.GetRequiredService<ScheduledAgentCredentialLifecycle>(),
            planner,
            new ScheduledInvocationAuthorizationRevalidator(planner, TimeProvider.System),
            effectiveOptions,
            timeProvider: TimeProvider.System);

        return new CreatorHarness(tool, handler, creationPort, queryPort, catalogCommandPort);
    }

    private static readonly NyxIdAuthorizationServiceEvidence[] DefaultAuthorizationServices =
    [
        ServiceEvidence("svc-ornn", "ornn-api"),
        ServiceEvidence("svc-lark", "api-lark-bot"),
        ServiceEvidence("svc-lark-failure", "api-lark-bot-inbound"),
        ServiceEvidence("svc-llm", "chrono-llm-public", "gpt-5.5"),
    ];

    private static RoutingJsonHandler CreateSuccessHandler(
        string createApiKeyResponse =
            """{"id":"key-created","full_key":"full-secret-key","purpose":"general","scheduled_write_enabled":false}""")
    {
        var handler = new RoutingJsonHandler();
        handler.Add(HttpMethod.Post, "/api/v1/api-keys", createApiKeyResponse);
        handler.Add(HttpMethod.Delete, "/api/v1/api-keys/key-created", """{"ok":true}""");
        return handler;
    }

    private static string CreateScopePlanResponse(string? requestBody)
    {
        using var request = JsonDocument.Parse(requestBody ?? throw new InvalidOperationException(
            "The scope-plan test request body is required."));
        var serviceIds = request.RootElement.GetProperty("selected_service_ids")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            authority = "nyxid",
            contract_version = "1",
            policy_version = "api-key-scope-v1",
            authenticated_actor = new { id = "nyx-user-1", type = "personal" },
            intended_key_owner = new { id = "nyx-user-1", type = "personal" },
            services = serviceIds.Select(static serviceId => new
            {
                user_service_id = serviceId,
                resource_owner = new { id = "nyx-user-1", type = "personal" },
                node_grant = new { type = "not_required" },
            }),
            allowed_service_ids = serviceIds,
            allowed_node_ids = Array.Empty<string>(),
            evaluated_at = "2026-07-21T00:00:00Z",
            normalized_grant_digest =
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            freshness = new
            {
                mode = "mutation_revalidated_snapshot",
                precondition_field = "scope_plan_digest",
                post_creation_drift = "fail_closed",
            },
            completeness = new
            {
                list_complete = true,
                no_duplicates = true,
                route_candidate_basis = "active_configured_routes",
                transient_node_state_excluded = true,
            },
        });
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
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, null),
        };

    private static AgentToolExecutionContext CreateToolContext(
        string? callerScopeId = "scope-bot-1",
        string? channelRegistrationScopeId = "scope-bot-1",
        string? channelSenderId = "ou_sender",
        string? channelDeliveryTargetId = null,
        IReadOnlyDictionary<string, string>? externalMetadata = null) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials("session-token", "session-token", null),
            Caller = new AgentToolCallerContext(callerScopeId, "nyx-user-1", "message-1"),
            Channel = new AgentToolChannelContext("lark", channelSenderId, channelRegistrationScopeId, "message-1", "om_1", channelDeliveryTargetId),
            SenderBinding = new AgentToolSenderBindingContext(
                "binding-lark-alpha",
                "nyx-user-1",
                "tenant-lark-alpha"),
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
            metadata[ChannelMetadataKeys.OutboundProviderSlug] = "api-lark-bot";
        if (includeUnionId)
        {
            metadata[ChannelMetadataKeys.DeliveryAddressId] = "on_union";
            metadata[ChannelMetadataKeys.DeliveryAddressType] = "union_id";
        }
        return metadata;
    }

    private static NyxIdAuthorizationCatalogSnapshot CreateSnapshot(
        params NyxIdAuthorizationServiceEvidence[] services)
    {
        var owner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = "nyx-user-1",
        };
        var clonedServices = services.Select(static service => service.Clone()).ToArray();
        var now = DateTimeOffset.UtcNow;
        return new NyxIdAuthorizationCatalogSnapshot(
            owner,
            23,
            now.AddMinutes(-1),
            now.AddMinutes(10),
            "1",
            "api-key-scope-v1",
            now.AddMinutes(-2),
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(owner, clonedServices),
            clonedServices,
            Activated: true);
    }

    private static NyxIdAuthorizationServiceEvidence ServiceEvidence(
        string id,
        string slug,
        params string[] modelIds)
    {
        var now = DateTimeOffset.UtcNow;
        return new NyxIdAuthorizationServiceEvidence
        {
            UserServiceId = id,
            ServiceSlug = slug,
            DisplayName = slug,
            Access = NyxIdAuthorizationAccess.Permitted,
            NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired,
            ResourceOwner = new AuthorizationOwnerIdentity
            {
                Authority = NyxIdAuthorizationAuthorities.NyxId,
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "nyx-user-1",
            },
            ObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddMinutes(-1)),
            FreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddMinutes(10)),
            EvaluatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddMinutes(-2)),
            AuthorityContractVersion = "1",
            AuthorityPolicyVersion = "api-key-scope-v1",
            LlmTarget = modelIds.Length == 0 ? null : ServiceLLMTarget(id, slug, modelIds),
        };
    }

    private static NyxIdAuthorizationCatalogSnapshot WithGatewayLLMTarget(
        NyxIdAuthorizationCatalogSnapshot snapshot,
        NyxIdAuthorizationLLMTargetEvidence target) =>
        snapshot with
        {
            ContentDigest = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
                snapshot.Owner,
                snapshot.Services,
                target),
            GatewayLLMTarget = target,
        };

    private static NyxIdAuthorizationLLMTargetEvidence GatewayLLMTarget(params string[] modelIds) =>
        LLMTarget(
            LLMRouteKind.Gateway,
            ScheduledInvocationOwnerLLMSelectionPolicy.GatewayRoute,
            string.Empty,
            string.Empty,
            modelIds);

    private static NyxIdAuthorizationLLMTargetEvidence ServiceLLMTarget(
        string serviceId,
        string serviceSlug,
        params string[] modelIds) =>
        LLMTarget(
            LLMRouteKind.NyxIdUserService,
            $"{ScheduledInvocationOwnerLLMSelectionPolicy.NyxIdProxyRoutePrefix}{serviceSlug}",
            serviceId,
            serviceSlug,
            modelIds);

    private static NyxIdAuthorizationLLMTargetEvidence LLMTarget(
        LLMRouteKind routeKind,
        string routeValue,
        string serviceId,
        string serviceSlug,
        params string[] modelIds)
    {
        var now = DateTimeOffset.UtcNow;
        var target = new NyxIdAuthorizationLLMTargetEvidence
        {
            RouteKind = routeKind,
            RouteValue = routeValue,
            NyxIdUserServiceId = serviceId,
            ServiceSlugSnapshot = serviceSlug,
            ModelCatalog = new LLMModelCatalog
            {
                Certainty = LLMModelCatalogCertainty.Enumerated,
                DefaultModelId = modelIds.FirstOrDefault() ?? string.Empty,
            },
            ObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddMinutes(-1)),
            FreshUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddMinutes(10)),
            EvaluatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddMinutes(-2)),
            AuthorityContractVersion = "openai-models/v1",
            AuthorityPolicyVersion = "nyxid-exact-route-models/v1",
        };
        target.ModelCatalog.ModelIds.Add(modelIds.Order(StringComparer.Ordinal));
        return target;
    }

    private sealed record CreatorHarness(
        ScheduledAgentCreatorTool Tool,
        RoutingJsonHandler Handler,
        IScheduledWorkflowAgentCreationPort CreationPort,
        IUserAgentCatalogQueryPort CatalogQueryPort,
        IUserAgentCatalogCommandPort CatalogCommandPort);

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

            if (_responses.TryGetValue($"{request.Method.Method}:{path}", out var response))
            {
                return new HttpResponseMessage(response.Status)
                {
                    Content = new StringContent(response.Json, Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Post && path == "/api/v1/api-keys/scope-plan")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        CreateScopePlanResponse(body),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"error":true,"message":"not found"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class FixedSnapshotQueryPort : INyxIdAuthorizationCatalogQueryPort
    {
        private readonly NyxIdAuthorizationCatalogSnapshot? _snapshot;

        public FixedSnapshotQueryPort(NyxIdAuthorizationCatalogSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class RecordingOwnerLLMEvidenceQueryPort(
        ScheduledInvocationOwnerLLMEvidence? evidence)
        : IScheduledInvocationOwnerLLMEvidenceQueryPort
    {
        public List<string> ScopeIds { get; } = [];

        public Task<ScheduledInvocationOwnerLLMEvidence?> GetAsync(
            string scopeId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScopeIds.Add(scopeId);
            return Task.FromResult(evidence);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string? Body,
        AuthenticationHeaderValue? Authorization);
}
