using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using Aevatar.AI.ToolProviders.AgentCatalog;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentDeliveryTargetToolTests
{
    [Fact]
    public void Name_Is_agent_delivery_targets()
    {
        var tool = CreateTool();
        tool.Name.Should().Be("agent_delivery_targets");
    }

    [Fact]
    public void ParametersSchema_Is_Valid_Json()
    {
        var tool = CreateTool();
        var act = () => JsonDocument.Parse(tool.ParametersSchema);
        act.Should().NotThrow();

        using var document = JsonDocument.Parse(tool.ParametersSchema);
        var properties = document.RootElement.GetProperty("properties");
        properties.TryGetProperty("nyx_api_key", out _).Should().BeFalse();
        properties.TryGetProperty("api_key_id", out _).Should().BeFalse();
        properties.TryGetProperty("allowed_service_ids", out _).Should().BeFalse();
        document.RootElement.GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString())
            .Should()
            .Contain("create");
        tool.Description.Should().Contain("without creating a scheduled runner");
        tool.Description.Should().Contain("different platform conversation");
        tool.Description.Should().NotContain("different Lark conversation");
        tool.Description.Should().NotContain("agent_builder tool's job");
    }

    [Fact]
    public async Task ExecuteAsync_Returns_Error_When_No_Auth_Token()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("""{"action":"list"}""");

        result.Should().Contain("error");
        result.Should().Contain("access token");
    }

    [Fact]
    public void Constructor_Requires_Typed_Dependencies()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();
        var secretVault = new InMemorySecretVault();

        var missingQuery = () => new AgentDeliveryTargetTool(null!, commandPort, resolver, secretVault);
        var missingCommand = () => new AgentDeliveryTargetTool(queryPort, null!, resolver, secretVault);
        var missingResolver = () => new AgentDeliveryTargetTool(queryPort, commandPort, null!, secretVault);
        var missingSecretVault = () => new AgentDeliveryTargetTool(queryPort, commandPort, resolver, null!);

        missingQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandPort");
        missingResolver.Should().Throw<ArgumentNullException>().WithParameterName("callerScopeResolver");
        missingSecretVault.Should().Throw<ArgumentNullException>().WithParameterName("secretVault");
    }

    [Fact]
    public async Task ExecuteAsync_List_DoesNotSurfaceCredentials()
    {
        // Issue #466 §D: the public DTO `UserAgentCatalogReadModelEntry` no longer carries the
        // NyxApiKey at all (not even masked). Credentials live behind the internal
        // `IUserAgentDeliveryTargetReader` and are not surfaced through any LLM tool.
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
                [
                new UserAgentCatalogReadModelEntry
                {
                    AgentId = "agent-1",
                    ConversationId = "oc_chat_1",
                    NyxProviderSlug = "api-lark-bot",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                },
            ]));

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list"}""");

            // Public DTO must not surface any credential field at all (no masked hint either).
            result.Should().NotContain("nyx_api_key");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
            var item = doc.RootElement.GetProperty("delivery_targets")[0];
            item.GetProperty("delivery_target_id").GetString().Should().Be("agent-1");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_Requires_AgentId()
    {
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>());
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"upsert"}""");
            result.Should().Contain("agent_id");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Create_MintsCredential_And_UpsertsCatalog()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("aelf-twitter-approval", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(null));
        queryPort.ExistsActiveAsync("aelf-twitter-approval", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        UserAgentCatalogUpsertCommand? captured = null;
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.UpsertAsync(Arg.Do<UserAgentCatalogUpsertCommand>(command => captured = command), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var issuer = new RecordingApiKeyIssuer();
        var secretVault = new InMemorySecretVault();
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(resolver);
        services.AddSingleton<IScheduledAgentApiKeyIssuer>(issuer);
        services.AddSingleton<ISecretVault>(secretVault);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create",
                  "delivery_target_id": "aelf-twitter-approval",
                  "platform": "lark",
                  "conversation_id": "oc_9f1b8d3835674963417954fad20f8a3c",
                  "nyx_provider_slug": "api-lark-bot-2"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("delivery_target_id").GetString().Should().Be("aelf-twitter-approval");
            doc.RootElement.GetProperty("api_key_id").GetString().Should().Be("key-aelf-twitter-approval");

            result.Should().NotContain("nyx_api_key");
            result.Should().NotContain("full_key");
            result.Should().NotContain("secret-created-key");

            issuer.Issues.Should().ContainSingle();
            issuer.Issues[0].AgentId.Should().Be("aelf-twitter-approval");
            issuer.Issues[0].ServiceSlugs.PrimaryOutboundSlug.Should().Be("api-lark-bot-2");
            issuer.Issues[0].ServiceSlugs.RequiresOrnnService.Should().BeFalse();
            issuer.Issues[0].SkillName.Should().BeEmpty();

            captured.Should().NotBeNull();
            captured!.AgentId.Should().Be("aelf-twitter-approval");
            captured.ConversationId.Should().Be("oc_9f1b8d3835674963417954fad20f8a3c");
            captured.NyxProviderSlug.Should().Be("api-lark-bot-2");
            captured.NyxApiKey.Should().BeEmpty();
            captured.NyxApiKeyReference.Should().NotBeNull();
            captured.NyxApiKeyReference!.Purpose.Should().Be(CredentialSecretPurposes.ScheduledNyxApiKey);
            captured.NyxApiKeyReference.Ref.Should().NotBe("secret-created-key");
            captured.NyxApiKeyReference.OwnerScopeKey.Should().NotBeNullOrWhiteSpace();
            captured.ApiKeyId.Should().Be("key-aelf-twitter-approval");
            captured.AgentType.Should().Be("delivery_target");
            captured.TemplateName.Should().Be("explicit_delivery_target");
            captured.TargetPlatform.Should().Be("lark");
            captured.LarkReceiveId.Should().BeEmpty();
            captured.LarkReceiveIdType.Should().BeEmpty();
            captured.OwnerScope.Should().NotBeNull();
            captured.OwnerScope!.MatchesStrictly(caller).Should().BeTrue();

            var resolved = await secretVault.ResolveAsync(new ResolveSecretRequest(
                captured.NyxApiKeyReference.Ref,
                CredentialSecretPurposes.ScheduledNyxApiKey,
                captured.NyxApiKeyReference.OwnerScopeKey,
                captured.ApiKeyId,
                "test"));
            resolved.Secret.Should().Be("secret-created-key");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Create_Accepts_NonLark_TargetPlatform_WithoutLarkFields()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("email-approval", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(null));

        UserAgentCatalogUpsertCommand? captured = null;
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.UpsertAsync(Arg.Do<UserAgentCatalogUpsertCommand>(command => captured = command), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var tool = new AgentDeliveryTargetTool(queryPort, commandPort, resolver, new InMemorySecretVault(), new RecordingApiKeyIssuer());

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create",
                  "delivery_target_id": "email-approval",
                  "platform": "email",
                  "conversation_id": "approvals@example.com",
                  "nyx_provider_slug": "api-email-outbound"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("platform").GetString().Should().Be("email");

            captured.Should().NotBeNull();
            captured!.TargetPlatform.Should().Be("email");
            captured.ConversationId.Should().Be("approvals@example.com");
            captured.NyxProviderSlug.Should().Be("api-email-outbound");
            captured.LarkReceiveId.Should().BeEmpty();
            captured.LarkReceiveIdType.Should().BeEmpty();
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Create_Rejects_Global_DeliveryTargetId_Collision_BeforeMintingCredential()
    {
        var caller = OwnerScope.ForNyxIdNative("user-2");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.ExistsActiveAsync("approvals", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var issuer = new RecordingApiKeyIssuer();
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var tool = new AgentDeliveryTargetTool(queryPort, commandPort, resolver, new InMemorySecretVault(), issuer);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create",
                  "delivery_target_id": "approvals",
                  "platform": "email",
                  "conversation_id": "approvals@example.com",
                  "nyx_provider_slug": "api-email-outbound"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("delivery_target_already_exists");
            doc.RootElement.GetProperty("delivery_target_id").GetString().Should().Be("approvals");
            issuer.Issues.Should().BeEmpty();
            await commandPort.DidNotReceive().UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Create_DoesNotRequireScheduledRunnerPort()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("aelf-twitter-approval", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(null));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var tool = new AgentDeliveryTargetTool(
            queryPort,
            commandPort,
            resolver,
            new InMemorySecretVault(),
            new RecordingApiKeyIssuer());

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "create",
                  "delivery_target_id": "aelf-twitter-approval",
                  "platform": "lark",
                  "conversation_id": "oc_chat_1",
                  "nyx_provider_slug": "api-lark-bot"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            await commandPort.Received(1).UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Create_RollsBackCredential_WhenCatalogDispatchFails()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("aelf-twitter-approval", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(null));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        commandPort.UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("dispatch failed"));

        var issuer = new RecordingApiKeyIssuer();
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var tool = new AgentDeliveryTargetTool(queryPort, commandPort, resolver, new InMemorySecretVault(), issuer);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var act = () => tool.ExecuteAsync("""
                {
                  "action": "create",
                  "delivery_target_id": "aelf-twitter-approval",
                  "platform": "lark",
                  "conversation_id": "oc_chat_1",
                  "nyx_provider_slug": "api-lark-bot"
                }
                """);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("dispatch failed");
            issuer.RevokedApiKeyIds.Should().ContainSingle().Which.Should().Be("key-aelf-twitter-approval");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_List_Shows_Created_DeliveryTarget_FromProjection()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
                [
                new UserAgentCatalogReadModelEntry
                {
                    AgentId = "aelf-twitter-approval",
                    ConversationId = "oc_9f1b8d3835674963417954fad20f8a3c",
                    NyxProviderSlug = "api-lark-bot-2",
                    LarkReceiveId = "oc_9f1b8d3835674963417954fad20f8a3c",
                    LarkReceiveIdType = "chat_id",
                    TargetPlatform = "lark",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                    AgentType = "delivery_target",
                },
            ]));

        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list"}""");

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
            var target = doc.RootElement.GetProperty("delivery_targets")[0];
            target.GetProperty("delivery_target_id").GetString().Should().Be("aelf-twitter-approval");
            target.GetProperty("platform").GetString().Should().Be("lark");
            target.GetProperty("conversation_id").GetString().Should().Be("oc_9f1b8d3835674963417954fad20f8a3c");
            target.GetProperty("nyx_provider_slug").GetString().Should().Be("api-lark-bot-2");
            result.Should().NotContain("secret");
            result.Should().NotContain("nyx_api_key");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_List_Uses_TargetPlatform_As_DeliveryPlatform()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(
                [
                new UserAgentCatalogReadModelEntry
                {
                    AgentId = "email-approval",
                    ConversationId = "approvals@example.com",
                    NyxProviderSlug = "api-email-outbound",
                    TargetPlatform = "email",
                    OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
                    AgentType = "delivery_target",
                },
            ]));

        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list"}""");

            using var doc = JsonDocument.Parse(result);
            var target = doc.RootElement.GetProperty("delivery_targets")[0];
            target.GetProperty("delivery_target_id").GetString().Should().Be("email-approval");
            target.GetProperty("platform").GetString().Should().Be("email");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_Forwards_Command_To_Port_And_Resolves_Current_User()
    {
        // Issue #466 §D: upsert is rebind-only — must reject when no existing entry exists.
        // Stub the queryPort to return a pre-existing entry so the rebind succeeds.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-1", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-1",
                ConversationId = "oc_chat_existing",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured an upsert result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; test asserts caller-scoped guard and command dispatch.
        commandPort.UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "upsert",
                  "agent_id": "agent-1",
                  "conversation_id": "oc_chat_1",
                  "nyx_provider_slug": "api-lark-bot"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("accepted")
                .And.Contain("propagating");

#pragma warning disable CS0612 // assert deprecated ownership fields are no longer emitted
            await commandPort.Received(1).UpsertAsync(
                Arg.Is<UserAgentCatalogUpsertCommand>(c =>
                    c.AgentId == "agent-1" &&
                    c.ConversationId == "oc_chat_1" &&
                    c.NyxProviderSlug == "api-lark-bot" &&
                    // Tool no longer accepts NyxApiKey as an argument; the credential
                    // is preserved through the actor's MergeNonEmpty upsert policy.
                    c.NyxApiKey == string.Empty &&
                    c.OwnerScope != null &&
                    c.OwnerScope.MatchesStrictly(caller) &&
                    c.Platform == string.Empty &&
                    c.OwnerNyxUserId == string.Empty),
                Arg.Any<CancellationToken>());
#pragma warning restore CS0612
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Requires_Confirm()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-2", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-2",
                ConversationId = "oc_chat_2",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>());
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-2"}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("confirm_required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Rejects_NonOwner()
    {
        // Issue #466 acceptance: caller-scoped existence check collapses non-owned ids
        // to "not found" (no existence disclosure). The query port's GetForCallerAsync
        // returns null when the caller does not own the requested id.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-2", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(null));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-2","confirm":true}""");

            result.Should().Contain("not found");
            await commandPort.DidNotReceive().TombstoneAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_Forwards_Tombstone_To_Port()
    {
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-3", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-3",
                ConversationId = "oc_chat_3",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; test asserts caller-scoped guard and command dispatch.
        commandPort.TombstoneAsync("agent-3", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-3","confirm":true}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");

            await commandPort.Received(1).TombstoneAsync("agent-3", Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_ReturnsAccepted_WhenCommandPortAccepts()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Delete surfaced "deleted" when the command port reported Observed.
        //   New principle: Delete returns accepted; callers confirm removal through list/get query paths.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-7", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-7",
                ConversationId = "oc_chat_7",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; accepted JSON remains a tool-boundary concern.
        commandPort.TombstoneAsync("agent-7", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(callerScopeResolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-7","confirm":true}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("accepted")
                .And.Contain("propagating");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ToolSource_Always_Returns_Tool()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var callerScopeResolver = Substitute.For<ICallerScopeResolver>();
        callerScopeResolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));
        queryPort.QueryByCallerAsync(Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserAgentCatalogReadModelEntry>>(Array.Empty<UserAgentCatalogReadModelEntry>()));

        var source = new AgentDeliveryTargetToolSource(queryPort, commandPort, callerScopeResolver, new InMemorySecretVault());
        var tools = await source.DiscoverToolsAsync();

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("agent_delivery_targets");

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tools[0].ExecuteAsync("""{"action":"list"}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);

            await queryPort.Received(1).QueryByCallerAsync(
                Arg.Any<OwnerScope>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    // ─── Patch coverage gap-fillers (issue #466 / codecov/patch) ───

    [Fact]
    public async Task ExecuteAsync_Returns_CallerScopeUnavailable_When_Resolver_Throws()
    {
        // Catches the ICallerScopeResolver.RequireAsync throw path: the tool surfaces
        // a structured `caller_scope_unavailable` error rather than falling through
        // (issue #466 fail-closed acceptance).
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns<Task<OwnerScope?>>(_ => throw new CallerScopeUnavailableException("test resolver failure"));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"list"}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("caller_scope_unavailable");
            doc.RootElement.GetProperty("detail").GetString().Should().Contain("test resolver failure");
            doc.RootElement.GetProperty("hint").GetString().Should().Contain("Re-authenticate");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public void ToolSource_Constructor_Requires_Typed_Dependencies()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();
        var secretVault = new InMemorySecretVault();

        var missingQuery = () => new AgentDeliveryTargetToolSource(null!, commandPort, resolver, secretVault);
        var missingCommand = () => new AgentDeliveryTargetToolSource(queryPort, null!, resolver, secretVault);
        var missingResolver = () => new AgentDeliveryTargetToolSource(queryPort, commandPort, null!, secretVault);
        var missingSecretVault = () => new AgentDeliveryTargetToolSource(queryPort, commandPort, resolver, null!);

        missingQuery.Should().Throw<ArgumentNullException>().WithParameterName("queryPort");
        missingCommand.Should().Throw<ArgumentNullException>().WithParameterName("commandPort");
        missingResolver.Should().Throw<ArgumentNullException>().WithParameterName("callerScopeResolver");
        missingSecretVault.Should().Throw<ArgumentNullException>().WithParameterName("secretVault");
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_Requires_ConversationId()
    {
        var (tool, _, _) = BuildBasicHarness();
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"upsert","agent_id":"agent-1"}""");
            result.Should().Contain("conversation_id");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_Requires_NyxProviderSlug()
    {
        var (tool, _, _) = BuildBasicHarness();
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {"action":"upsert","agent_id":"agent-1","conversation_id":"oc_chat_1"}
                """);
            result.Should().Contain("nyx_provider_slug");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_RejectsCreateWhenNoExistingEntry()
    {
        // Issue #466 review: upsert is rebind-only. When no existing entry exists for
        // the caller, fail closed with `delivery_target_not_found_for_caller` instead
        // of dispatching a credential-less upsert command.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-new", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(null));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "upsert",
                  "agent_id": "agent-new",
                  "conversation_id": "oc_chat_new",
                  "nyx_provider_slug": "api-lark-bot"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("error").GetString().Should().Be("delivery_target_not_found_for_caller");
            doc.RootElement.GetProperty("hint").GetString().Should().Contain("rebind");
            await commandPort.DidNotReceive().UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Upsert_ReturnsAccepted_WhenCommandPortReportsAccepted()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Test covered the !Observed branch after hidden projection polling timed out.
        //   New principle: Upsert always returns accepted plus a propagating note.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-pending", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-pending",
                ConversationId = "oc_chat_old",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured an upsert result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; accepted JSON remains a tool-boundary concern.
        commandPort.UpsertAsync(Arg.Any<UserAgentCatalogUpsertCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""
                {
                  "action": "upsert",
                  "agent_id": "agent-pending",
                  "conversation_id": "oc_chat_new",
                  "nyx_provider_slug": "api-lark-bot"
                }
                """);

            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("note").GetString()
                .Should().Contain("accepted")
                .And.Contain("propagating");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_RequiresAgentId()
    {
        var (tool, _, _) = BuildBasicHarness();
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete"}""");
            result.Should().Contain("agent_id");
            result.Should().Contain("required");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Delete_ReturnsAccepted_WhenCommandPortReportsAccepted()
    {
        // Refactor (iter4/cluster-009):
        //   Old pattern: Test covered the !Observed branch after hidden projection polling timed out.
        //   New principle: Delete always returns accepted plus a propagating note after the caller-scoped pre-check.
        var caller = OwnerScope.ForNyxIdNative("user-1");

        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        queryPort.GetForCallerAsync("agent-slow", Arg.Any<OwnerScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogReadModelEntry?>(new UserAgentCatalogReadModelEntry
            {
                AgentId = "agent-slow",
                ConversationId = "oc_chat_slow",
                NyxProviderSlug = "api-lark-bot",
                OwnerScope = caller,
            }));

        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        // Refactor (iter5/cluster-012):
        //   Old pattern: Stub manufactured a tombstone result just to satisfy a dead return shape.
        //   New principle: Stub returns Task.CompletedTask; accepted JSON remains a tool-boundary concern.
        commandPort.TombstoneAsync("agent-slow", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(caller));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(resolver);
        var tool = CreateTool(services);

        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "session-token",
        });
        try
        {
            var result = await tool.ExecuteAsync("""{"action":"delete","agent_id":"agent-slow","confirm":true}""");
            using var doc = JsonDocument.Parse(result);
            doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
            doc.RootElement.GetProperty("note").GetString().Should().Contain("propagating");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    /// <summary>
    /// Minimal harness for tests that only need the early required-field validation
    /// branches (no real query/command response wiring). Returns a tool with a stub
    /// query port, a stub command port, and a deterministic caller-scope resolver.
    /// </summary>
    private static AgentDeliveryTargetTool CreateTool(IServiceCollection? services = null)
    {
        var provider = (services ?? CreateDefaultServices()).BuildServiceProvider();
        return new AgentDeliveryTargetTool(
            provider.GetRequiredService<IUserAgentCatalogQueryPort>(),
            provider.GetService<IUserAgentCatalogCommandPort>() ?? Substitute.For<IUserAgentCatalogCommandPort>(),
            provider.GetRequiredService<ICallerScopeResolver>(),
            provider.GetService<ISecretVault>() ?? new InMemorySecretVault(),
            provider.GetService<IScheduledAgentApiKeyIssuer>());
    }

    private static IServiceCollection CreateDefaultServices()
    {
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        return new ServiceCollection()
            .AddSingleton(Substitute.For<IUserAgentCatalogQueryPort>())
            .AddSingleton(Substitute.For<IUserAgentCatalogCommandPort>())
            .AddSingleton<ISecretVault>(new InMemorySecretVault())
            .AddSingleton(resolver);
    }

    private static (AgentDeliveryTargetTool tool, IUserAgentCatalogQueryPort queryPort, IUserAgentCatalogCommandPort commandPort) BuildBasicHarness()
    {
        var queryPort = Substitute.For<IUserAgentCatalogQueryPort>();
        var commandPort = Substitute.For<IUserAgentCatalogCommandPort>();
        var resolver = Substitute.For<ICallerScopeResolver>();
        resolver.TryResolveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScope?>(OwnerScope.ForNyxIdNative("user-1")));

        var services = new ServiceCollection();
        services.AddSingleton(queryPort);
        services.AddSingleton(commandPort);
        services.AddSingleton(resolver);
        return (CreateTool(services), queryPort, commandPort);
    }

    private sealed class RecordingApiKeyIssuer : IScheduledAgentApiKeyIssuer
    {
        public List<IssueCall> Issues { get; } = [];
        public List<string> RevokedApiKeyIds { get; } = [];

        public Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
            string token,
            ScheduledAgentServiceSlugs serviceSlugs,
            string agentId,
            string skillName,
            string? scopeId,
            CancellationToken ct)
        {
            Issues.Add(new IssueCall(token, serviceSlugs, agentId, skillName, scopeId));
            return Task.FromResult(ScheduledAgentApiKeyIssueResult.Succeeded($"key-{agentId}", "secret-created-key"));
        }

        public Task TryRevokeAsync(string token, string apiKeyId, CancellationToken ct)
        {
            RevokedApiKeyIds.Add(apiKeyId);
            return Task.CompletedTask;
        }
    }

    private sealed record IssueCall(
        string Token,
        ScheduledAgentServiceSlugs ServiceSlugs,
        string AgentId,
        string SkillName,
        string? ScopeId);
}
