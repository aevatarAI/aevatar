using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.AgentCatalog;
using Aevatar.AI.ToolProviders.AgentCatalog.AgentProfiles;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentProfilesToolTests
{
    private static readonly string[] Actions =
    [
        "create",
        "get",
        "update_draft",
        "upsert_skill",
        "remove_skill",
        "validate",
        "publish",
    ];

    [Fact]
    public void Metadata_exposes_only_the_strict_profile_management_schema()
    {
        var tool = CreateTool(out _, out _);

        tool.Name.Should().Be("agent_profiles");
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        var root = schema.RootElement;
        root.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        root.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().BeEquivalentTo("action", "profile_slug");
        root.GetProperty("properties").GetProperty("action").GetProperty("enum")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal(Actions);

        root.GetProperty("properties").EnumerateObject()
            .Select(static property => property.Name)
            .Should().BeEquivalentTo(
                "action",
                "profile_slug",
                "owner_handle",
                "display_name",
                "purpose",
                "instructions",
                "tool_policy",
                "etag",
                "binding_id",
                "activation_mode",
                "skill",
                "idempotency_key");

        var skill = root.GetProperty("properties").GetProperty("skill");
        skill.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        skill.GetProperty("properties").EnumerateObject()
            .Select(static property => property.Name)
            .Should().BeEquivalentTo(
                "skill_guid",
                "literal_version",
                "expected_name",
                "expected_publisher_id");
        skill.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().BeEquivalentTo(
                "skill_guid",
                "literal_version",
                "expected_name",
                "expected_publisher_id");

        var forbiddenProperties = new[]
        {
            "scope_id",
            "owner_subject",
            "subject_id",
            "identity_provider",
            "profile_id",
            "system_owner",
            "system_authority",
            "platform_id",
            "authority_state_version",
            "if_match",
            "skip_etag",
            "inline_content",
            "skill_name",
            "sealed_content",
            "sealed_body",
            "access_token",
            "credentials",
            "latest",
        };
        foreach (var forbidden in forbiddenProperties)
            tool.ParametersSchema.Should().NotContain($"\"{forbidden}\"");
    }

    [Fact]
    public async Task Constructors_and_source_require_typed_application_contracts()
    {
        var commands = Substitute.For<IAgentProfileCommandService>();
        var queries = Substitute.For<IAgentProfileQueryService>();

        var missingCommands = () => new AgentProfilesTool(null!, queries);
        var missingQueries = () => new AgentProfilesTool(commands, null!);
        var missingSourceCommands = () => new AgentProfilesToolSource(null!, queries);
        var missingSourceQueries = () => new AgentProfilesToolSource(commands, null!);

        missingCommands.Should().Throw<ArgumentNullException>().WithParameterName("commands");
        missingQueries.Should().Throw<ArgumentNullException>().WithParameterName("queries");
        missingSourceCommands.Should().Throw<ArgumentNullException>().WithParameterName("commands");
        missingSourceQueries.Should().Throw<ArgumentNullException>().WithParameterName("queries");

        var source = new AgentProfilesToolSource(commands, queries);
        var tools = await source.DiscoverToolsAsync();
        tools.Should().ContainSingle().Which.Should().BeOfType<AgentProfilesTool>();
    }

    [Fact]
    public void AddAgentCatalogTools_registers_both_sources_once()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>());
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(Substitute.For<ICallerScopeResolver>());
        services.AddSingleton(Substitute.For<ISecretVault>());
        services.AddSingleton(Substitute.For<IAgentProfileCommandService>());
        services.AddSingleton(Substitute.For<IAgentProfileQueryService>());

        services.AddAgentCatalogTools();
        services.AddAgentCatalogTools();

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IAgentToolSource>()
            .Select(static source => source.GetType())
            .Should().Equal(
                typeof(AgentDeliveryTargetToolSource),
                typeof(AgentProfilesToolSource));
    }

    [Fact]
    public async Task Deferred_source_discovery_does_not_fabricate_missing_profile_services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>());
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(Substitute.For<ICallerScopeResolver>());
        services.AddSingleton(Substitute.For<ISecretVault>());
        services.AddAgentCatalogTools();

        using var provider = services.BuildServiceProvider();
        var source = provider.GetServices<IAgentToolSource>()
            .OfType<AgentProfilesToolSource>()
            .Single();
        var tool = (await source.DiscoverToolsAsync()).Should().ContainSingle().Which;
        using var context = UseContext(CreateContext());

        var act = () => tool.ExecuteAsync(
            """{ "action": "get", "profile_slug": "profile-zulu" }""");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IAgentProfileQueryService*");
    }

    [Fact]
    public async Task ExecuteAsync_rejects_unknown_action_without_calling_application()
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync("""
            { "action": "delete", "profile_slug": "profile-zulu" }
            """);

        Error(result).Should().Be("invalid_agent_profile_action");
        AssertNoApplicationCalls(commands, queries);
    }

    [Theory]
    [InlineData("""{ "action": "get", "profile_slug": "profile-zulu", "owner_handle": "forged-owner" }""")]
    [InlineData("""{ "action": "get", "profile_slug": "profile-zulu", "scope_id": "forged-scope" }""")]
    [InlineData("""{ "action": "validate", "profile_slug": "profile-zulu", "inline_content": "forged-body" }""")]
    [InlineData("""{ "action": "get", "profileSlug": "profile-zulu", "profile_slug": "profile-zulu" }""")]
    [InlineData("""{ "action": "remove_skill", "profile_slug": "profile-zulu", "etag": "\"agent-profile-v23\"", "binding_id": "binding-alpha", "skill": {} }""")]
    public async Task ExecuteAsync_rejects_unknown_or_action_inapplicable_fields(string argumentsJson)
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync(argumentsJson);

        Error(result).Should().Be("invalid_agent_profile_arguments");
        AssertNoApplicationCalls(commands, queries);
    }

    [Fact]
    public async Task ExecuteAsync_requires_caller_scope()
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateContext(scopeId: null));

        var result = await tool.ExecuteAsync("""{ "action": "get", "profile_slug": "profile-zulu" }""");

        Error(result).Should().Be("agent_profile_scope_required");
        AssertNoApplicationCalls(commands, queries);
    }

    [Fact]
    public async Task ExecuteAsync_requires_caller_subject()
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateContext(subjectId: null));

        var result = await tool.ExecuteAsync("""{ "action": "get", "profile_slug": "profile-zulu" }""");

        Error(result).Should().Be("agent_profile_subject_required");
        AssertNoApplicationCalls(commands, queries);
    }

    [Fact]
    public async Task ExecuteAsync_channel_context_uses_complete_bound_sender_authority_pair()
    {
        var tool = CreateTool(out var commands, out var queries);
        AgentProfileCallerContext? capturedCaller = null;
        queries.GetOwnedAsync(
                Arg.Do<AgentProfileCallerContext>(caller => capturedCaller = caller),
                "profile-zulu",
                Arg.Any<CancellationToken>())
            .Returns(ManagementSnapshot());
        using var context = UseContext(CreateChannelContext());

        await tool.ExecuteAsync("""{ "action": "get", "profile_slug": "profile-zulu" }""");

        capturedCaller.Should().NotBeNull();
        capturedCaller!.ScopeId.Should().Be("scope-sender-owner-61");
        capturedCaller.Owner.SubjectId.Should().Be("sender-nyx-user-67");
        capturedCaller.NyxIdAccessToken.Should().Be("sender-token-secret-71");
        capturedCaller.ScopeId.Should().NotBe("scope-bot-registration-53");
        capturedCaller.Owner.SubjectId.Should().NotBe("bot-owner-subject-59");
        commands.ReceivedCalls().Should().BeEmpty();
        queries.ReceivedCalls().Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_sender_binding_without_channel_sender_uses_sender_authority_pair()
    {
        var tool = CreateTool(out _, out var queries);
        AgentProfileCallerContext? capturedCaller = null;
        queries.GetOwnedAsync(
                Arg.Do<AgentProfileCallerContext>(caller => capturedCaller = caller),
                "profile-zulu",
                Arg.Any<CancellationToken>())
            .Returns(ManagementSnapshot());
        using var context = UseContext(CreateChannelContext(channelSenderId: null));

        await tool.ExecuteAsync("""{ "action": "get", "profile_slug": "profile-zulu" }""");

        capturedCaller.Should().NotBeNull();
        capturedCaller!.ScopeId.Should().Be("scope-sender-owner-61");
        capturedCaller.Owner.SubjectId.Should().Be("sender-nyx-user-67");
        capturedCaller.NyxIdAccessToken.Should().Be("sender-token-secret-71");
    }

    [Fact]
    public async Task ExecuteAsync_channel_sender_without_sender_token_fails_closed_before_get()
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateChannelContext(senderAccessToken: null));

        var result = await tool.ExecuteAsync(
            """{ "action": "get", "profile_slug": "profile-zulu" }""");

        Error(result).Should().Be("agent_profile_sender_authority_required");
        result.Should().NotContain("bot-token-secret-43");
        result.Should().NotContain("secret");
        AssertNoApplicationCalls(commands, queries);
    }

    [Fact]
    public async Task ExecuteAsync_binding_only_without_sender_token_fails_closed_before_get()
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateChannelContext(
            channelSenderId: null,
            senderAccessToken: null));

        var result = await tool.ExecuteAsync(
            """{ "action": "get", "profile_slug": "profile-zulu" }""");

        Error(result).Should().Be("agent_profile_sender_authority_required");
        result.Should().NotContain("bot-token-secret-43");
        result.Should().NotContain("secret");
        AssertNoApplicationCalls(commands, queries);
    }

    [Fact]
    public async Task ExecuteAsync_unbound_channel_get_fails_closed_without_reading_owner_profile()
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateChannelContext(
            bindingId: null,
            senderNyxUserId: null,
            senderOwnerScopeId: null));

        var result = await tool.ExecuteAsync(
            """{ "action": "get", "profile_slug": "profile-zulu" }""");

        Error(result).Should().Be("agent_profile_sender_authority_required");
        AssertNoApplicationCalls(commands, queries);
        result.Should().NotContain("bot-owner-subject-59");
        result.Should().NotContain("bot-token-secret-43");
    }

    [Theory]
    [InlineData(null, "sender-nyx-user-67", "scope-sender-owner-61")]
    [InlineData("binding-sender-73", null, "scope-sender-owner-61")]
    [InlineData("binding-sender-73", "sender-nyx-user-67", null)]
    public async Task ExecuteAsync_incomplete_channel_sender_authority_fails_closed(
        string? bindingId,
        string? senderNyxUserId,
        string? senderOwnerScopeId)
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateChannelContext(
            bindingId,
            senderNyxUserId,
            senderOwnerScopeId));

        var result = await tool.ExecuteAsync(
            """{ "action": "get", "profile_slug": "profile-zulu" }""");

        Error(result).Should().Be("agent_profile_sender_authority_required");
        AssertNoApplicationCalls(commands, queries);
    }

    [Theory]
    [MemberData(nameof(VersionedMutationsWithoutEtag))]
    public async Task ExecuteAsync_requires_etag_for_each_versioned_mutation(string argumentsJson)
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync(argumentsJson);

        Error(result).Should().Be("agent_profile_etag_required");
        AssertNoApplicationCalls(commands, queries);
    }

    public static TheoryData<string> VersionedMutationsWithoutEtag => new()
    {
        ValidUpdateDraftArguments(includeEtag: false),
        ValidUpsertSkillArguments(includeEtag: false),
        """{ "action": "remove_skill", "profile_slug": "profile-zulu", "binding_id": "binding-alpha" }""",
        """{ "action": "publish", "profile_slug": "profile-zulu" }""",
    };

    [Theory]
    [InlineData("agent-profile-v23")]
    [InlineData("\"agent-profile-v023\"")]
    [InlineData("W/\"agent-profile-v23\"")]
    [InlineData("\"agent-profile-v-1\"")]
    public async Task ExecuteAsync_rejects_noncanonical_or_weak_etag(string etag)
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateContext());
        var arguments = JsonSerializer.Serialize(new
        {
            action = "remove_skill",
            profile_slug = "profile-zulu",
            binding_id = "binding-alpha",
            etag,
        });

        var result = await tool.ExecuteAsync(arguments);

        Error(result).Should().Be("invalid_agent_profile_etag");
        AssertNoApplicationCalls(commands, queries);
    }

    [Theory]
    [InlineData("validate", """{ "action": "validate", "profile_slug": "profile-zulu" }""")]
    [InlineData("publish", """{ "action": "publish", "profile_slug": "profile-zulu", "etag": "\"agent-profile-v23\"" }""")]
    public async Task ExecuteAsync_requires_access_token_for_validate_and_publish(
        string action,
        string argumentsJson)
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateContext(accessToken: null));

        var result = await tool.ExecuteAsync(argumentsJson);

        Error(result).Should().Be("ornn_access_token_required", action);
        AssertNoApplicationCalls(commands, queries);
    }

    [Fact]
    public async Task ExecuteAsync_requires_create_idempotency_key()
    {
        var tool = CreateTool(out var commands, out var queries);
        using var context = UseContext(CreateContext(idempotencyKey: null));

        var result = await tool.ExecuteAsync(ValidCreateArguments(includeIdempotencyKey: false));

        Error(result).Should().Be("idempotency_key_required");
        AssertNoApplicationCalls(commands, queries);
    }

    [Fact]
    public async Task ExecuteAsync_create_maps_canonical_caller_content_and_explicit_idempotency_once()
    {
        var tool = CreateTool(out var commands, out var queries);
        AgentProfileCallerContext? capturedCaller = null;
        CreateAgentProfileRequest? capturedRequest = null;
        string? capturedIdempotencyKey = null;
        commands.CreateAsync(
                Arg.Do<AgentProfileCallerContext>(caller => capturedCaller = caller),
                Arg.Do<CreateAgentProfileRequest>(request => capturedRequest = request),
                Arg.Do<string>(key => capturedIdempotencyKey = key),
                Arg.Any<CancellationToken>())
            .Returns(AcceptedReceipt());
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync(ValidCreateArguments(includeIdempotencyKey: true));

        AssertCanonicalCaller(capturedCaller);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ProfileSlug.Should().Be("profile-zulu");
        capturedRequest.OwnerHandle.Should().Be("owner-delta");
        capturedRequest.DisplayName.Should().Be("Support Profile");
        capturedRequest.Purpose.Should().Be("Resolve support requests");
        capturedRequest.Instructions.Should().Be("Answer with verified facts.");
        capturedRequest.ToolPolicy.Mode.Should().Be(AgentProfileToolPolicyMode.ExplicitAllowlist);
        capturedRequest.ToolPolicy.ToolNames.Should().Equal("ornn_search_skills", "agent_profiles");
        capturedRequest.ToolPolicy.ToolSetRefs.Should().Equal("workspace.default");
        capturedIdempotencyKey.Should().Be("explicit-idempotency-61");
        AssertAcceptedReceipt(result);
        result.Should().NotContain("token-secret-47");
        result.Should().NotContain("credentials");
        commands.ReceivedCalls().Should().ContainSingle();
        queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_create_uses_request_context_idempotency_fallback()
    {
        var tool = CreateTool(out var commands, out _);
        string? capturedIdempotencyKey = null;
        commands.CreateAsync(
                Arg.Any<AgentProfileCallerContext>(),
                Arg.Any<CreateAgentProfileRequest>(),
                Arg.Do<string>(key => capturedIdempotencyKey = key),
                Arg.Any<CancellationToken>())
            .Returns(AcceptedReceipt());
        using var context = UseContext(CreateContext(idempotencyKey: "context-idempotency-72"));

        await tool.ExecuteAsync(ValidCreateArguments(includeIdempotencyKey: false));

        capturedIdempotencyKey.Should().Be("context-idempotency-72");
    }

    [Fact]
    public async Task ExecuteAsync_get_calls_owned_query_once_and_returns_canonical_reference_and_strong_etag()
    {
        var tool = CreateTool(out var commands, out var queries);
        AgentProfileCallerContext? capturedCaller = null;
        queries.GetOwnedAsync(
                Arg.Do<AgentProfileCallerContext>(caller => capturedCaller = caller),
                "profile-zulu",
                Arg.Any<CancellationToken>())
            .Returns(ManagementSnapshot());
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync("""{ "action": "get", "profile_slug": "profile-zulu" }""");

        AssertCanonicalCaller(capturedCaller);
        var payload = Parse(result);
        payload.GetProperty("etag").GetString().Should().Be("\"agent-profile-v23\"");
        payload.GetProperty("authority_state_version").GetInt64().Should().Be(23);
        payload.GetProperty("profile_id").GetString().Should().Be("profile-internal-83");
        payload.GetProperty("reference").GetProperty("owner_handle").GetString().Should().Be("owner-delta");
        payload.GetProperty("reference").GetProperty("profile_slug").GetString().Should().Be("profile-zulu");
        payload.GetProperty("draft_digest").GetString().Should().Be("64726166742d646967657374");
        payload.GetProperty("draft").GetProperty("skill_bindings")[0]
            .GetProperty("skill").GetProperty("skill_guid").GetString().Should().Be("guid-stable-11");
        result.Should().NotContain("subject-bravo-29");
        result.Should().NotContain("token-secret-47");
        queries.ReceivedCalls().Should().ContainSingle();
        commands.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_get_returns_stable_not_found_error()
    {
        var tool = CreateTool(out _, out var queries);
        queries.GetOwnedAsync(
                Arg.Any<AgentProfileCallerContext>(),
                "profile-zulu",
                Arg.Any<CancellationToken>())
            .Returns((AgentProfileManagementSnapshot?)null);
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync("""{ "action": "get", "profile_slug": "profile-zulu" }""");

        Error(result).Should().Be("agent_profile_not_found");
    }

    [Fact]
    public async Task ExecuteAsync_update_draft_maps_if_match_and_content_once()
    {
        var tool = CreateTool(out var commands, out var queries);
        long capturedVersion = -1;
        UpdateAgentProfileDraftRequest? capturedRequest = null;
        string? capturedIdempotencyKey = null;
        commands.UpdateDraftAsync(
                Arg.Any<AgentProfileCallerContext>(),
                "profile-zulu",
                Arg.Do<long>(version => capturedVersion = version),
                Arg.Do<UpdateAgentProfileDraftRequest>(request => capturedRequest = request),
                Arg.Do<string?>(key => capturedIdempotencyKey = key),
                Arg.Any<CancellationToken>())
            .Returns(AcceptedReceipt());
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync(ValidUpdateDraftArguments(includeEtag: true));

        capturedVersion.Should().Be(23);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.DisplayName.Should().Be("Updated Support Profile");
        capturedRequest.ToolPolicy.Mode.Should().Be(AgentProfileToolPolicyMode.InheritRouteMaximum);
        capturedIdempotencyKey.Should().Be("mutation-key-91");
        AssertAcceptedReceipt(result);
        commands.ReceivedCalls().Should().ContainSingle();
        queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_upsert_skill_maps_only_exact_ornn_reference_once()
    {
        var tool = CreateTool(out var commands, out var queries);
        UpsertAgentProfileSkillBindingRequest? capturedRequest = null;
        long capturedVersion = -1;
        commands.UpsertSkillBindingAsync(
                Arg.Any<AgentProfileCallerContext>(),
                "profile-zulu",
                "binding-alpha",
                Arg.Do<long>(version => capturedVersion = version),
                Arg.Do<UpsertAgentProfileSkillBindingRequest>(request => capturedRequest = request),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(AcceptedReceipt());
        using var context = UseContext(CreateContext(accessToken: null));

        var result = await tool.ExecuteAsync(ValidUpsertSkillArguments(includeEtag: true));

        capturedVersion.Should().Be(23);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ActivationMode.Should().Be(AgentProfileSkillActivationMode.Routed);
        capturedRequest.Skill.SkillGuid.Should().Be("guid-stable-11");
        capturedRequest.Skill.LiteralVersion.Should().Be("2.7");
        capturedRequest.Skill.ExpectedName.Should().Be("support-research");
        capturedRequest.Skill.ExpectedPublisherId.Should().Be("publisher-stable-31");
        AssertAcceptedReceipt(result);
        commands.ReceivedCalls().Should().ContainSingle();
        queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_remove_skill_maps_binding_and_if_match_once()
    {
        var tool = CreateTool(out var commands, out var queries);
        string? capturedBindingId = null;
        long capturedVersion = -1;
        commands.RemoveSkillBindingAsync(
                Arg.Any<AgentProfileCallerContext>(),
                "profile-zulu",
                Arg.Do<string>(bindingId => capturedBindingId = bindingId),
                Arg.Do<long>(version => capturedVersion = version),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(AcceptedReceipt());
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync("""
            {
              "action": "remove_skill",
              "profile_slug": "profile-zulu",
              "binding_id": "binding-alpha",
              "etag": "\"agent-profile-v23\""
            }
            """);

        capturedBindingId.Should().Be("binding-alpha");
        capturedVersion.Should().Be(23);
        AssertAcceptedReceipt(result);
        commands.ReceivedCalls().Should().ContainSingle();
        queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_validate_maps_tokenized_caller_once_and_returns_safe_report()
    {
        var tool = CreateTool(out var commands, out var queries);
        AgentProfileCallerContext? capturedCaller = null;
        commands.ValidateAsync(
                Arg.Do<AgentProfileCallerContext>(caller => capturedCaller = caller),
                "profile-zulu",
                Arg.Any<CancellationToken>())
            .Returns(ValidationReport());
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync("""{ "action": "validate", "profile_slug": "profile-zulu" }""");

        AssertCanonicalCaller(capturedCaller);
        var payload = Parse(result);
        payload.GetProperty("valid").GetBoolean().Should().BeTrue();
        payload.GetProperty("draft_revision").GetInt64().Should().Be(7);
        payload.GetProperty("draft_digest").GetString().Should().Be("76616c6964617465642d6472616674");
        payload.GetProperty("resolved_skills")[0].GetProperty("exact_reference")
            .GetProperty("expected_publisher_id").GetString().Should().Be("publisher-stable-31");
        result.Should().NotContain("token-secret-47");
        commands.ReceivedCalls().Should().ContainSingle();
        queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_publish_maps_if_match_once_and_returns_accepted_only_receipt()
    {
        var tool = CreateTool(out var commands, out var queries);
        AgentProfileCallerContext? capturedCaller = null;
        long capturedVersion = -1;
        commands.PublishAsync(
                Arg.Do<AgentProfileCallerContext>(caller => capturedCaller = caller),
                "profile-zulu",
                Arg.Do<long>(version => capturedVersion = version),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(AcceptedReceipt());
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync("""
            {
              "action": "publish",
              "profile_slug": "profile-zulu",
              "etag": "\"agent-profile-v23\""
            }
            """);

        AssertCanonicalCaller(capturedCaller);
        capturedVersion.Should().Be(23);
        AssertAcceptedReceipt(result);
        result.Contains("committed", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        commands.ReceivedCalls().Should().ContainSingle();
        queries.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_rejects_receipt_that_claims_more_than_accepted()
    {
        var tool = CreateTool(out var commands, out _);
        commands.CreateAsync(
                Arg.Any<AgentProfileCallerContext>(),
                Arg.Any<CreateAgentProfileRequest>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(AcceptedReceipt() with { AckStage = "committed" });
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync(ValidCreateArguments(includeIdempotencyKey: true));

        Error(result).Should().Be("agent_profile_dispatch_rejected");
        result.Contains("committed", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_maps_known_service_boundary_exception_to_safe_error()
    {
        var tool = CreateTool(out _, out var queries);
        var exception = new AgentProfileRequestException(
            "AGENT_PROFILE_BOUNDARY_REJECTED",
            [
                new AgentProfileSafeDiagnostic
                {
                    Code = "SAFE_PROFILE_DIAGNOSTIC",
                    Message = "The Profile request was rejected.",
                    Path = "draft.skills[0]",
                },
            ]);
        exception.Data["unsafe-detail"] = "service-secret-211";
        queries.GetOwnedAsync(
                Arg.Any<AgentProfileCallerContext>(),
                "profile-zulu",
                Arg.Any<CancellationToken>())
            .Returns<Task<AgentProfileManagementSnapshot?>>(_ => throw exception);
        using var context = UseContext(CreateContext());

        var result = await tool.ExecuteAsync(
            """{ "action": "get", "profile_slug": "profile-zulu" }""");

        var payload = Parse(result);
        payload.GetProperty("error").GetString().Should().Be("AGENT_PROFILE_BOUNDARY_REJECTED");
        payload.GetProperty("diagnostics")[0].GetProperty("code").GetString()
            .Should().Be("SAFE_PROFILE_DIAGNOSTIC");
        payload.GetProperty("diagnostics")[0].GetProperty("message").GetString()
            .Should().Be("The Profile request was rejected.");
        payload.GetProperty("diagnostics")[0].GetProperty("path").GetString()
            .Should().Be("draft.skills[0]");
        result.Should().NotContain("service-secret-211");
        result.Should().NotContain(nameof(AgentProfileRequestException));
    }

    [Fact]
    public async Task ExecuteAsync_serializes_only_typed_profile_boundary_and_json_errors()
    {
        var tool = CreateTool(out var commands, out var queries);
        queries.GetOwnedAsync(
                Arg.Any<AgentProfileCallerContext>(),
                "profile-zulu",
                Arg.Any<CancellationToken>())
            .Returns<Task<AgentProfileManagementSnapshot?>>(_ => throw new AgentProfileContractValidationException(
            [
                new AgentProfileSafeDiagnostic
                {
                    Code = "SAFE_PROFILE_BOUNDARY_ERROR",
                    Message = "bounded diagnostic",
                    Path = "draft",
                },
            ]));
        using var context = UseContext(CreateContext());

        var typed = await tool.ExecuteAsync("""{ "action": "get", "profile_slug": "profile-zulu" }""");
        var malformed = await tool.ExecuteAsync("{not-json");

        Error(typed).Should().Be("SAFE_PROFILE_BOUNDARY_ERROR");
        typed.Should().NotContain("bounded diagnostic");
        Error(malformed).Should().Be("invalid_agent_profile_arguments");
        commands.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_does_not_serialize_unknown_failures()
    {
        var tool = CreateTool(out _, out var queries);
        queries.GetOwnedAsync(
                Arg.Any<AgentProfileCallerContext>(),
                "profile-zulu",
                Arg.Any<CancellationToken>())
            .Returns<Task<AgentProfileManagementSnapshot?>>(_ => throw new InvalidOperationException("secret-store-failed"));
        using var context = UseContext(CreateContext());

        var act = () => tool.ExecuteAsync("""{ "action": "get", "profile_slug": "profile-zulu" }""");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("secret-store-failed");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_serialize_cancellation()
    {
        var tool = CreateTool(out _, out var queries);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        queries.GetOwnedAsync(
                Arg.Any<AgentProfileCallerContext>(),
                "profile-zulu",
                Arg.Any<CancellationToken>())
            .Returns<Task<AgentProfileManagementSnapshot?>>(_ => throw new OperationCanceledException(cts.Token));
        using var context = UseContext(CreateContext());

        var act = () => tool.ExecuteAsync(
            """{ "action": "get", "profile_slug": "profile-zulu" }""",
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static AgentProfilesTool CreateTool(
        out IAgentProfileCommandService commands,
        out IAgentProfileQueryService queries)
    {
        commands = Substitute.For<IAgentProfileCommandService>();
        queries = Substitute.For<IAgentProfileQueryService>();
        return new AgentProfilesTool(commands, queries);
    }

    private static AgentToolExecutionContext CreateContext(
        string? scopeId = "scope-alpha-17",
        string? subjectId = "subject-bravo-29",
        string? accessToken = "token-secret-47",
        string? idempotencyKey = "context-idempotency-53") =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-charlie-37", "call-delta-41", idempotencyKey),
            Credentials = new AgentToolCredentials(accessToken, null, null),
            Caller = new AgentToolCallerContext(scopeId, subjectId, "response-echo-43"),
        };

    private static AgentToolExecutionContext CreateChannelContext(
        string? bindingId = "binding-sender-73",
        string? senderNyxUserId = "sender-nyx-user-67",
        string? senderOwnerScopeId = "scope-sender-owner-61",
        string? channelSenderId = "platform-sender-83",
        string? senderAccessToken = "sender-token-secret-71") =>
        CreateContext(
            scopeId: "scope-bot-registration-53",
            subjectId: "bot-owner-subject-59",
            accessToken: "bot-token-secret-43") with
        {
            Credentials = new AgentToolCredentials(
                "bot-token-secret-43",
                "bot-token-secret-43",
                senderAccessToken),
            Caller = new AgentToolCallerContext(
                "scope-bot-registration-53",
                "bot-owner-subject-59",
                "response-channel-79",
                senderOwnerScopeId),
            Channel = new AgentToolChannelContext(
                "lark",
                channelSenderId,
                "scope-bot-registration-53",
                "message-channel-89",
                "platform-message-97"),
            SenderBinding = new AgentToolSenderBindingContext(
                bindingId,
                senderNyxUserId,
                "sender-tenant-101"),
        };

    private static IDisposable UseContext(AgentToolExecutionContext context) => new ToolContextScope(context);

    private static void AssertCanonicalCaller(AgentProfileCallerContext? caller)
    {
        caller.Should().NotBeNull();
        caller!.ScopeId.Should().Be("scope-alpha-17");
        caller.Owner.IdentityProvider.Should().Be("nyxid");
        caller.Owner.SubjectId.Should().Be("subject-bravo-29");
        caller.NyxIdAccessToken.Should().Be("token-secret-47");
    }

    private static void AssertNoApplicationCalls(
        IAgentProfileCommandService commands,
        IAgentProfileQueryService queries)
    {
        commands.ReceivedCalls().Should().BeEmpty();
        queries.ReceivedCalls().Should().BeEmpty();
    }

    private static void AssertAcceptedReceipt(string result)
    {
        var payload = Parse(result);
        payload.GetProperty("accepted").GetBoolean().Should().BeTrue();
        payload.GetProperty("ack_stage").GetString().Should().Be("accepted");
        payload.GetProperty("operation_id").GetString().Should().Be("operation-101");
        payload.GetProperty("command_id").GetString().Should().Be("command-103");
        payload.GetProperty("correlation_id").GetString().Should().Be("correlation-107");
        payload.GetProperty("actor_id").GetString().Should().Be("actor-109");
        payload.GetProperty("profile_id").GetString().Should().Be("profile-internal-83");
        payload.GetProperty("resource_url").GetString().Should().Be(
            "/api/scopes/scope-alpha-17/agent-profiles/profile-zulu");
    }

    private static string Error(string result) => Parse(result).GetProperty("error").GetString()!;

    private static JsonElement Parse(string result)
    {
        using var document = JsonDocument.Parse(result);
        return document.RootElement.Clone();
    }

    private static AgentProfileAcceptedReceipt AcceptedReceipt() => new(
        Accepted: true,
        AckStage: "accepted",
        OperationId: "operation-101",
        CommandId: "command-103",
        CorrelationId: "correlation-107",
        ActorId: "actor-109",
        ProfileId: "profile-internal-83",
        ResourceUrl: "/api/scopes/scope-alpha-17/agent-profiles/profile-zulu");

    private static AgentProfileManagementSnapshot ManagementSnapshot()
    {
        var content = new AgentProfileContent
        {
            DisplayName = "Support Profile",
            Purpose = "Resolve support requests",
            Instructions = "Answer with verified facts.",
            ToolPolicy = new AgentProfileToolPolicy
            {
                Mode = AgentProfileToolPolicyMode.ExplicitAllowlist,
            },
        };
        content.ToolPolicy.ToolNames.Add("agent_profiles");
        content.ToolPolicy.ToolSetRefs.Add("workspace.default");
        content.SkillBindings.Add(new AgentProfileSkillBinding
        {
            BindingId = "binding-alpha",
            ActivationMode = AgentProfileSkillActivationMode.Routed,
            Skill = ExactReference(),
        });
        return new AgentProfileManagementSnapshot(
            AuthorityStateVersion: 23,
            LastEventId: "event-113",
            Identity: new AgentProfileIdentity
            {
                ProfileId = "profile-internal-83",
                Owner = new AgentProfileOwnerIdentity
                {
                    User = new AgentProfileUserOwnerIdentity
                    {
                        IdentityProvider = "nyxid",
                        SubjectId = "subject-bravo-29",
                    },
                },
                OwningScopeId = "scope-alpha-17",
                Reference = new AgentProfileReference
                {
                    OwnerHandle = "owner-delta",
                    ProfileSlug = "profile-zulu",
                },
            },
            Draft: content,
            DraftRevision: 5,
            DraftSha256: ByteString.CopyFromUtf8("draft-digest"),
            PublishedRevision: 3,
            PublishedSnapshotSha256: ByteString.CopyFromUtf8("published-digest"),
            PublishedSourceDraftSha256: ByteString.CopyFromUtf8("source-draft"),
            LastMutation: null);
    }

    private static AgentProfileValidationReport ValidationReport() => new(
        Valid: true,
        DraftRevision: 7,
        DraftSha256: ByteString.CopyFromUtf8("validated-draft"),
        Diagnostics:
        [
            new AgentProfileSafeDiagnostic
            {
                Code = "PROFILE_VALID",
                Message = "Draft is valid.",
                Path = "draft",
            },
        ],
        ResolvedSkills:
        [
            new AgentProfileSkillResolutionSummary(
                "binding-alpha",
                ExactReference(),
                ByteString.CopyFromUtf8("skill-content")),
        ]);

    private static ExactOrnnSkillReference ExactReference() => new()
    {
        SkillGuid = "guid-stable-11",
        LiteralVersion = "2.7",
        ExpectedName = "support-research",
        ExpectedPublisherId = "publisher-stable-31",
    };

    private static string ValidCreateArguments(bool includeIdempotencyKey)
    {
        var idempotency = includeIdempotencyKey
            ? "\n  \"idempotency_key\": \"explicit-idempotency-61\","
            : string.Empty;
        return $$"""
            {
              "action": "create",{{idempotency}}
              "profile_slug": "profile-zulu",
              "owner_handle": "owner-delta",
              "display_name": "Support Profile",
              "purpose": "Resolve support requests",
              "instructions": "Answer with verified facts.",
              "tool_policy": {
                "mode": "EXPLICIT_ALLOWLIST",
                "tool_names": ["ornn_search_skills", "agent_profiles"],
                "tool_set_refs": ["workspace.default"]
              }
            }
            """;
    }

    private static string ValidUpdateDraftArguments(bool includeEtag)
    {
        var etag = includeEtag ? "\n  \"etag\": \"\\\"agent-profile-v23\\\"\"," : string.Empty;
        return $$"""
            {
              "action": "update_draft",{{etag}}
              "profile_slug": "profile-zulu",
              "display_name": "Updated Support Profile",
              "purpose": "Resolve updated support requests",
              "instructions": "Use current verified facts.",
              "tool_policy": {
                "mode": "INHERIT_ROUTE_MAXIMUM",
                "tool_names": [],
                "tool_set_refs": []
              },
              "idempotency_key": "mutation-key-91"
            }
            """;
    }

    private static string ValidUpsertSkillArguments(bool includeEtag)
    {
        var etag = includeEtag ? "\n  \"etag\": \"\\\"agent-profile-v23\\\"\"," : string.Empty;
        return $$"""
            {
              "action": "upsert_skill",{{etag}}
              "profile_slug": "profile-zulu",
              "binding_id": "binding-alpha",
              "activation_mode": "ROUTED",
              "skill": {
                "skill_guid": "guid-stable-11",
                "literal_version": "2.7",
                "expected_name": "support-research",
                "expected_publisher_id": "publisher-stable-31"
              }
            }
            """;
    }

    private sealed class ToolContextScope : IDisposable
    {
        private readonly AgentToolExecutionContext? _previous = AgentToolRequestContext.Current;

        public ToolContextScope(AgentToolExecutionContext context) => AgentToolRequestContext.Current = context;

        public void Dispose() => AgentToolRequestContext.Current = _previous;
    }
}
