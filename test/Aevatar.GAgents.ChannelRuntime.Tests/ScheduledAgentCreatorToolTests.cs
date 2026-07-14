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
using Aevatar.Studio.Application.Authorization;
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

    [Fact]
    public async Task ExecuteAsync_WhenCommittedOwnerSnapshotIsUnavailable_ShouldFailBeforeSideEffects()
    {
        var harness = CreateHarness(snapshotUnavailable: true);

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
    public async Task ExecuteAsync_WhenRequiredServiceMissing_ShouldFailClosedWithoutBroadKey()
    {
        var harness = CreateHarness(snapshot: CreateSnapshot(
            new NyxIdServiceGrant { UserServiceId = "svc-lark", ServiceSlug = "api-lark-bot", NodeGrantsNotRequired = true }));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("ServiceNotFound");
            document.RootElement.GetProperty("detail").GetString().Should().Be("nyxid_service_slug_not_unique:ornn-api");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenRequiredServiceAmbiguous_ShouldFailClosedWithoutKeyCreation()
    {
        var harness = CreateHarness(snapshot: CreateSnapshot(
            new NyxIdServiceGrant { UserServiceId = "svc-ornn-1", ServiceSlug = "ornn-api", NodeGrantsNotRequired = true },
            new NyxIdServiceGrant { UserServiceId = "svc-ornn-2", ServiceSlug = "ornn-api", NodeGrantsNotRequired = true },
            new NyxIdServiceGrant { UserServiceId = "svc-lark", ServiceSlug = "api-lark-bot", NodeGrantsNotRequired = true },
            new NyxIdServiceGrant { UserServiceId = "svc-lark-failure", ServiceSlug = "api-lark-bot-inbound", NodeGrantsNotRequired = true }));

        await WithToolContext(async () =>
        {
            var result = await harness.Tool.ExecuteAsync(BaseArgs);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("ServiceNotFound");
            document.RootElement.GetProperty("detail").GetString().Should().Be("nyxid_service_slug_not_unique:ornn-api");
            harness.Handler.Requests.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task ExecuteAsync_RequiredServiceSlugs_ShouldBeResolvedIntoScopedKeyAllowlist()
    {
        var handler = CreateSuccessHandler();
        var harness = CreateHarness(handler: handler, snapshot: CreateSnapshot(
            DefaultGrants.Append(new NyxIdServiceGrant { UserServiceId = "svc-tavily", ServiceSlug = "tavily-search", NodeGrantsNotRequired = true })
                .Append(new NyxIdServiceGrant { UserServiceId = "svc-github", ServiceSlug = "api-github", NodeGrantsNotRequired = true }).ToArray()));

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
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Get);
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
                  "required_service_slugs": ["tavily-search"]
                }
                """);

            using var document = JsonDocument.Parse(result);
            document.RootElement.GetProperty("error").GetString().Should().Be("ServiceNotFound");
            document.RootElement.GetProperty("detail").GetString().Should().Be("nyxid_service_slug_not_unique:tavily-search");
            handler.Requests.Should().BeEmpty();
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
            document.RootElement.GetProperty("hint").GetString().Should().Contain("owned");
            handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Delete);
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());
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
            captured.Schedule.Headers["scheduled_agent.output_format"].Should().Be(SkillRunnerOutputFormat.FeishuDoc.ToString());
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
            captured.CatalogEntry.ChannelAddress.Primary.AddressId.Should().Be("oc_chat");
            captured.CatalogEntry.ChannelAddress.Primary.AddressType.Should().Be("chat_id");
            captured.CatalogEntry.ChannelAddress.Fallback.Should().NotBeNull();
            captured.CatalogEntry.ChannelAddress.Fallback!.AddressId.Should().Be("on_union");
            captured.CatalogEntry.ChannelAddress.Fallback.AddressType.Should().Be("union_id");
#pragma warning disable CS0612 // verifies new writes leave deprecated command fields empty
            captured.CatalogEntry.LarkReceiveId.Should().BeEmpty();
            captured.CatalogEntry.LarkReceiveIdType.Should().BeEmpty();
            captured.CatalogEntry.LarkReceiveIdFallback.Should().BeEmpty();
            captured.CatalogEntry.LarkReceiveIdTypeFallback.Should().BeEmpty();
#pragma warning restore CS0612
            captured.CatalogEntry.OutputFormat.Should().Be(SkillRunnerOutputFormat.FeishuDoc);
            captured.CatalogEntry.OwnerScope.MatchesStrictly(caller).Should().BeTrue();
            await harness.SkillRunnerPort.DidNotReceive().InitializeAsync(
                Arg.Any<string>(),
                Arg.Any<InitializeSkillRunnerCommand>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>());

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

            harness.Handler.Requests.Should().NotContain(request => request.Method == HttpMethod.Get);

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
                  "one_shot_message": "Send my daily report"
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
                  "one_shot_message": "Remind me to join the meeting"
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
            var createRequest = handler.Requests.Single(request => request.Method == HttpMethod.Post);
            using var createBody = JsonDocument.Parse(createRequest.Body!);
            createBody.RootElement.GetProperty("allowed_service_ids").EnumerateArray().Select(static x => x.GetString())
                .Should().BeEquivalentTo("svc-lark", "svc-lark-failure");
        });
    }

    [Fact]
    public async Task ExecuteAsync_WhenInitializeFails_ShouldRequestDurableCredentialRevocation()
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
            harness.Handler.Requests.Should().NotContain(request =>
                request.Method == HttpMethod.Delete &&
                request.Path == "/api/v1/api-keys/key-created");
            await harness.CatalogCommandPort.Received(1).RequestCredentialRevocationAsync(
                Arg.Is<ScheduledAgentCredentialRevocationIntent>(intent =>
                    intent.ApiKeyId == "key-created" &&
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
          "schedule_timezone": "UTC"
        }
        """;

    private static CreatorHarness CreateHarness(
        RoutingJsonHandler? handler = null,
        OwnerScope? scope = null,
        bool callerScopeUnavailable = false,
        IOwnerLlmConfigSource? ownerLlmConfigSource = null,
        ISecretVault? secretVault = null,
        ScheduledAgentCreatorOptions? options = null,
        NyxIdCatalogSnapshot? snapshot = default,
        bool snapshotUnavailable = false)
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

        var services = new ServiceCollection();
        services.AddSingleton<INyxIdApiClientFactory>(nyxClientFactory);
        services.AddSingleton(skillRunnerPort);
        services.AddSingleton(creationPort);
        services.AddSingleton(resolver);
        services.AddSingleton(queryPort);
        var catalogCommandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        services.AddSingleton(catalogCommandPort);
        services.AddSingleton(options ?? new ScheduledAgentCreatorOptions());
        services.AddSingleton<ScheduledAgentCreateRequestMapper>();
        services.AddSingleton<ISecretVault>(secretVault ?? new InMemorySecretVault());
        if (ownerLlmConfigSource is not null)
            services.AddSingleton(ownerLlmConfigSource);
        services.AddSingleton<ScheduledAgentApiKeyIssuer>();
        services.AddSingleton<IScheduledAgentApiKeyIssuer>(sp => sp.GetRequiredService<ScheduledAgentApiKeyIssuer>());
        services.AddSingleton<IScheduledAgentCredentialLifecycle, ScheduledAgentCredentialLifecycle>();

        var provider = services.BuildServiceProvider();
        var tool = new ScheduledAgentCreatorTool(
            provider.GetRequiredService<IScheduledWorkflowAgentCreationPort>(),
            provider.GetRequiredService<ICallerScopeResolver>(),
            provider.GetRequiredService<ScheduledAgentCreateRequestMapper>(),
            provider.GetRequiredService<IScheduledAgentCredentialLifecycle>(),
            new ScheduledInvocationAuthorizationPlanner(
                new FixedSnapshotQueryPort(snapshotUnavailable ? null : snapshot ?? CreateSnapshot(DefaultGrants)),
                Substitute.For<IScheduledInvocationMemberQueryPort>(),
                Substitute.For<IScheduledInvocationWorkflowQueryPort>(),
                Substitute.For<IScheduledInvocationConnectorQueryPort>(),
                Substitute.For<IScheduledInvocationOwnerLLMQueryPort>()),
            provider.GetRequiredService<ScheduledAgentCreatorOptions>());

        return new CreatorHarness(tool, handler, skillRunnerPort, creationPort, queryPort, catalogCommandPort);
    }

    private static readonly NyxIdServiceGrant[] DefaultGrants =
    [
        new() { UserServiceId = "svc-ornn", ServiceSlug = "ornn-api", NodeGrantsNotRequired = true },
        new() { UserServiceId = "svc-lark", ServiceSlug = "api-lark-bot", NodeGrantsNotRequired = true },
        new() { UserServiceId = "svc-lark-failure", ServiceSlug = "api-lark-bot-inbound", NodeGrantsNotRequired = true },
    ];

    private static NyxIdCatalogSnapshot CreateSnapshot(params NyxIdServiceGrant[] grants)
    {
        var now = DateTimeOffset.UtcNow;
        return new NyxIdCatalogSnapshot(
            new NyxIdCatalogOwnerIdentity
            {
                Authority = "nyxid",
                OwnerKind = NyxIdCatalogOwnerKind.Personal,
                OwnerSubject = "nyx-user-1",
            },
            17,
            now,
            now.AddMinutes(10),
            "catalog-r17",
            "catalog-digest-r17",
            grants);
    }

    private static RoutingJsonHandler CreateSuccessHandler(
        string createApiKeyResponse = """{"id":"key-created","full_key":"full-secret-key"}""")
    {
        var handler = new RoutingJsonHandler();
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
            Caller = new AgentToolCallerContext(callerScopeId, "nyx-user-1", "message-1"),
            Channel = new AgentToolChannelContext("lark", channelSenderId, channelRegistrationScopeId, "message-1", "om_1"),
            SenderBinding = new AgentToolSenderBindingContext("binding-lark-alpha", "nyx-user-1", "tenant-lark-alpha"),
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

        public string? GetResponse(HttpMethod method, string path) =>
            _responses.TryGetValue($"{method.Method}:{path}", out var response) ? response.Json : null;

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

    private sealed class FixedSnapshotQueryPort : INyxIdCatalogSnapshotQueryPort
    {
        private readonly NyxIdCatalogSnapshot? _snapshot;

        public FixedSnapshotQueryPort(NyxIdCatalogSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<NyxIdCatalogSnapshot?> GetAsync(NyxIdCatalogOwnerIdentity owner, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshot);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string? Body,
        AuthenticationHeaderValue? Authorization);
}
