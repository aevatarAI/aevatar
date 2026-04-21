using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class RegistrationQueryPortTests
{
    // ─── DeviceRegistrationQueryPort ───

    [Fact]
    public async Task DeviceQueryPort_GetAsync_ReturnsEntry_WhenDocumentExists()
    {
        var reader = Substitute.For<IProjectionDocumentReader<DeviceRegistrationDocument, string>>();
        reader.GetAsync("reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeviceRegistrationDocument?>(new DeviceRegistrationDocument
            {
                Id = "reg-1",
                ScopeId = "scope-a",
                HmacKey = "key-abc",
                NyxConversationId = "conv-42",
                Description = "Test device",
                StateVersion = 3,
                LastEventId = "evt-1",
                ActorId = "actor-1",
            }));

        var queryPort = new DeviceRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("reg-1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("reg-1");
        result.ScopeId.Should().Be("scope-a");
        result.HmacKey.Should().Be("key-abc");
        result.NyxConversationId.Should().Be("conv-42");
        result.Description.Should().Be("Test device");
    }

    [Fact]
    public async Task DeviceQueryPort_GetAsync_ReturnsNull_WhenDocumentNotFound()
    {
        var reader = Substitute.For<IProjectionDocumentReader<DeviceRegistrationDocument, string>>();
        reader.GetAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DeviceRegistrationDocument?>(null));

        var queryPort = new DeviceRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeviceQueryPort_GetAsync_ReturnsNull_ForBlankId()
    {
        var reader = Substitute.For<IProjectionDocumentReader<DeviceRegistrationDocument, string>>();
        var queryPort = new DeviceRegistrationQueryPort(reader);

        var result = await queryPort.GetAsync("");

        result.Should().BeNull();
        // Reader should not be called for blank IDs
        await reader.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeviceQueryPort_QueryAllAsync_ReturnsMappedEntries()
    {
        var reader = Substitute.For<IProjectionDocumentReader<DeviceRegistrationDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<DeviceRegistrationDocument>
            {
                Items =
                [
                    new DeviceRegistrationDocument
                    {
                        Id = "reg-1", ScopeId = "scope-a", HmacKey = "k1",
                        ActorId = "a1", StateVersion = 1, LastEventId = "e1",
                    },
                    new DeviceRegistrationDocument
                    {
                        Id = "reg-2", ScopeId = "scope-b", HmacKey = "k2",
                        ActorId = "a1", StateVersion = 2, LastEventId = "e2",
                    },
                ],
            }));

        var queryPort = new DeviceRegistrationQueryPort(reader);
        var result = await queryPort.QueryAllAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("reg-1");
        result[1].Id.Should().Be("reg-2");
    }

    [Fact]
    public async Task DeviceQueryPort_QueryAllAsync_ReturnsEmpty_WhenNoDocuments()
    {
        var reader = Substitute.For<IProjectionDocumentReader<DeviceRegistrationDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ProjectionDocumentQueryResult<DeviceRegistrationDocument>.Empty));

        var queryPort = new DeviceRegistrationQueryPort(reader);
        var result = await queryPort.QueryAllAsync();

        result.Should().BeEmpty();
    }

    // ─── ChannelBotRegistrationQueryPort ───

    [Fact]
    public async Task BotQueryPort_GetAsync_ReturnsEntry_WhenDocumentExists()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-1",
                Platform = "lark",
                NyxProviderSlug = "lark-provider",
                NyxUserToken = "token-abc",
                VerificationToken = "verify-123",
                ScopeId = "scope-x",
                WebhookUrl = "https://example.com/callback/bot-1",
                StateVersion = 2,
                LastEventId = "evt-bot-1",
                ActorId = "actor-bot",
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("bot-1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("bot-1");
        result.Platform.Should().Be("lark");
        result.NyxProviderSlug.Should().Be("lark-provider");
        result.NyxUserToken.Should().Be("token-abc");
        result.VerificationToken.Should().Be("verify-123");
        result.ScopeId.Should().Be("scope-x");
        result.WebhookUrl.Should().Be("https://example.com/callback/bot-1");
    }

    [Fact]
    public async Task BotQueryPort_GetAsync_PropagatesCredentialRef()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-credref", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-credref",
                Platform = "lark",
                CredentialRef = "secrets://lark/encrypt-key/bot-credref",
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("bot-credref");

        result.Should().NotBeNull();
        result!.CredentialRef.Should().Be("secrets://lark/encrypt-key/bot-credref");
    }

    [Fact]
    public async Task BotQueryPort_GetAsync_ReturnsNull_WhenDocumentNotFound()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(null));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task BotQueryPort_GetAsync_ReturnsNull_ForBlankId()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        var queryPort = new ChannelBotRegistrationQueryPort(reader);

        var result = await queryPort.GetAsync("   ");

        result.Should().BeNull();
        await reader.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BotQueryPort_QueryAllAsync_ReturnsMappedEntries()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<ChannelBotRegistrationDocument>
            {
                Items =
                [
                    new ChannelBotRegistrationDocument
                    {
                        Id = "bot-1", Platform = "lark", NyxProviderSlug = "lark-p",
                        ActorId = "a1", StateVersion = 1, LastEventId = "e1",
                    },
                    new ChannelBotRegistrationDocument
                    {
                        Id = "bot-2", Platform = "telegram", NyxProviderSlug = "tg-p",
                        ActorId = "a1", StateVersion = 2, LastEventId = "e2",
                    },
                ],
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.QueryAllAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("bot-1");
        result[0].Platform.Should().Be("lark");
        result[1].Id.Should().Be("bot-2");
        result[1].Platform.Should().Be("telegram");
    }

    [Fact]
    public async Task BotQueryPort_GetStateVersionAsync_ReturnsVersion_WhenDocumentExists()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-1",
                StateVersion = 42,
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetStateVersionAsync("bot-1");

        result.Should().Be(42);
    }

    [Fact]
    public async Task BotQueryPort_GetStateVersionAsync_ReturnsNull_WhenDocumentNotFound()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(null));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetStateVersionAsync("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task BotQueryPort_GetStateVersionAsync_ReturnsNull_ForBlankId()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        var queryPort = new ChannelBotRegistrationQueryPort(reader);

        var result = await queryPort.GetStateVersionAsync("");

        result.Should().BeNull();
        await reader.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BotQueryPort_GetAsync_PropagatesEncryptKey()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-enc", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-enc",
                Platform = "lark",
                NyxProviderSlug = "lark-provider",
                NyxUserToken = "token-abc",
                VerificationToken = "verify-123",
                ScopeId = "scope-x",
                WebhookUrl = "https://example.com/callback",
                EncryptKey = "my-secret-encrypt-key",
                StateVersion = 5,
                LastEventId = "evt-enc",
                ActorId = "actor-bot",
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("bot-enc");

        result.Should().NotBeNull();
        result!.EncryptKey.Should().Be("my-secret-encrypt-key");
    }

    [Fact]
    public async Task BotQueryPort_GetAsync_DefaultsEncryptKeyToEmpty_WhenNull()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-no-enc", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-no-enc",
                Platform = "lark",
                StateVersion = 1,
                LastEventId = "evt-1",
                ActorId = "actor-bot",
                // EncryptKey not set — proto default is empty string
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("bot-no-enc");

        result.Should().NotBeNull();
        result!.EncryptKey.Should().BeEmpty();
    }

    [Fact]
    public async Task BotQueryPort_QueryAllAsync_PropagatesEncryptKey()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<ChannelBotRegistrationDocument>
            {
                Items =
                [
                    new ChannelBotRegistrationDocument
                    {
                        Id = "bot-a", Platform = "lark", EncryptKey = "key-a",
                        ActorId = "a1", StateVersion = 1, LastEventId = "e1",
                    },
                    new ChannelBotRegistrationDocument
                    {
                        Id = "bot-b", Platform = "lark", EncryptKey = "key-b",
                        ActorId = "a1", StateVersion = 2, LastEventId = "e2",
                    },
                ],
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.QueryAllAsync();

        result.Should().HaveCount(2);
        result[0].EncryptKey.Should().Be("key-a");
        result[1].EncryptKey.Should().Be("key-b");
    }

    [Fact]
    public async Task BotQueryPort_QueryAllAsync_ReturnsEmpty_WhenNoDocuments()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ProjectionDocumentQueryResult<ChannelBotRegistrationDocument>.Empty));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.QueryAllAsync();

        result.Should().BeEmpty();
    }

    // ─── UserAgentCatalogQueryPort ───

    [Fact]
    public async Task UserAgentCatalogQueryPort_GetAsync_ReturnsEntry_WhenDocumentExists()
    {
        var reader = Substitute.For<IProjectionDocumentReader<AgentRegistryDocument, string>>();
        reader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryDocument?>(new AgentRegistryDocument
            {
                Id = "agent-1",
                Platform = "lark",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
                NyxApiKey = "nyx-key-1",
                OwnerNyxUserId = "user-1",
                AgentType = "skill_runner",
                TemplateName = "daily_report",
                ScopeId = "scope-1",
                ApiKeyId = "key-1",
                ScheduleCron = "0 9 * * *",
                ScheduleTimezone = "UTC",
                Status = "running",
                LastRunAtUtc = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 14, 8, 0, 0, TimeSpan.Zero)),
                NextRunAtUtc = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 4, 15, 9, 0, 0, TimeSpan.Zero)),
                ErrorCount = 2,
                LastError = "last-error",
                StateVersion = 7,
                ActorId = "agent-registry-store",
            }));

        var queryPort = new UserAgentCatalogQueryPort(reader);
        var result = await queryPort.GetAsync("agent-1");

        result.Should().NotBeNull();
        result!.AgentId.Should().Be("agent-1");
        result.Platform.Should().Be("lark");
        result.ConversationId.Should().Be("oc_chat_1");
        result.NyxProviderSlug.Should().Be("api-lark-bot");
        result.NyxApiKey.Should().Be("nyx-key-1");
        result.OwnerNyxUserId.Should().Be("user-1");
        result.AgentType.Should().Be("skill_runner");
        result.TemplateName.Should().Be("daily_report");
        result.ScopeId.Should().Be("scope-1");
        result.ApiKeyId.Should().Be("key-1");
        result.ScheduleCron.Should().Be("0 9 * * *");
        result.ScheduleTimezone.Should().Be("UTC");
        result.Status.Should().Be("running");
        result.LastRunAt.Should().NotBeNull();
        result.NextRunAt.Should().NotBeNull();
        result.ErrorCount.Should().Be(2);
        result.LastError.Should().Be("last-error");
    }

    [Fact]
    public async Task UserAgentCatalogQueryPort_GetAsync_ReturnsNull_WhenTombstoned()
    {
        var reader = Substitute.For<IProjectionDocumentReader<AgentRegistryDocument, string>>();
        reader.GetAsync("agent-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryDocument?>(new AgentRegistryDocument
            {
                Id = "agent-2",
                Tombstoned = true,
                StateVersion = 8,
            }));

        var queryPort = new UserAgentCatalogQueryPort(reader);
        var result = await queryPort.GetAsync("agent-2");

        result.Should().BeNull();
    }

    [Fact]
    public async Task UserAgentCatalogQueryPort_QueryAllAsync_FiltersTombstonedEntries()
    {
        var reader = Substitute.For<IProjectionDocumentReader<AgentRegistryDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<AgentRegistryDocument>
            {
                Items =
                [
                    new AgentRegistryDocument
                    {
                        Id = "agent-a",
                        Platform = "lark",
                        ConversationId = "oc_a",
                        NyxApiKey = "key-a",
                        AgentType = "skill_runner",
                        TemplateName = "daily_report",
                        StateVersion = 1,
                    },
                    new AgentRegistryDocument
                    {
                        Id = "agent-b",
                        Platform = "lark",
                        ConversationId = "oc_b",
                        NyxApiKey = "key-b",
                        Tombstoned = true,
                        StateVersion = 2,
                    },
                ],
            }));

        var queryPort = new UserAgentCatalogQueryPort(reader);
        var result = await queryPort.QueryAllAsync();

        result.Should().ContainSingle();
        result[0].AgentId.Should().Be("agent-a");
        result[0].NyxApiKey.Should().Be("key-a");
        result[0].AgentType.Should().Be("skill_runner");
        result[0].TemplateName.Should().Be("daily_report");
    }

    [Fact]
    public async Task UserAgentCatalogQueryPort_GetStateVersionAsync_ReturnsVersion_WhenDocumentExists()
    {
        var reader = Substitute.For<IProjectionDocumentReader<AgentRegistryDocument, string>>();
        reader.GetAsync("agent-3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentRegistryDocument?>(new AgentRegistryDocument
            {
                Id = "agent-3",
                StateVersion = 11,
            }));

        var queryPort = new UserAgentCatalogQueryPort(reader);
        var result = await queryPort.GetStateVersionAsync("agent-3");

        result.Should().Be(11);
    }
}
