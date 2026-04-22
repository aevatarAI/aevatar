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
    public async Task BotQueryPort_GetAsync_ReturnsPublicEntry_WhenDocumentExists()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-1",
                Platform = "lark",
                NyxProviderSlug = "lark-provider",
                ScopeId = "scope-x",
                WebhookUrl = "https://example.com/callback/bot-1",
                NyxChannelBotId = "nyx-bot-1",
                NyxAgentApiKeyId = "key-1",
                NyxConversationRouteId = "route-1",
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
        result.ScopeId.Should().Be("scope-x");
        result.WebhookUrl.Should().Be("https://example.com/callback/bot-1");
        result.NyxChannelBotId.Should().Be("nyx-bot-1");
        result.NyxAgentApiKeyId.Should().Be("key-1");
        result.NyxConversationRouteId.Should().Be("route-1");
    }

    [Fact]
    public async Task BotQueryPort_GetAsync_DoesNotExposeLegacyDirectBinding()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-public", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-public",
                Platform = "lark",
                ScopeId = "scope-public",
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("bot-public");

        result.Should().NotBeNull();
        result!.LegacyDirectBinding.Should().BeNull();
        result.NyxUserToken.Should().BeEmpty();
        result.NyxRefreshToken.Should().BeEmpty();
        result.VerificationToken.Should().BeEmpty();
        result.CredentialRef.Should().BeEmpty();
        result.EncryptKey.Should().BeEmpty();
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
                        Id = "bot-1",
                        Platform = "lark",
                        NyxProviderSlug = "lark-p",
                        NyxChannelBotId = "channel-bot-1",
                        ActorId = "a1",
                        StateVersion = 1,
                        LastEventId = "e1",
                    },
                    new ChannelBotRegistrationDocument
                    {
                        Id = "bot-2",
                        Platform = "telegram",
                        NyxProviderSlug = "tg-p",
                        NyxAgentApiKeyId = "api-key-2",
                        ActorId = "a1",
                        StateVersion = 2,
                        LastEventId = "e2",
                    },
                ],
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.QueryAllAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("bot-1");
        result[0].Platform.Should().Be("lark");
        result[0].NyxChannelBotId.Should().Be("channel-bot-1");
        result[0].LegacyDirectBinding.Should().BeNull();
        result[1].Id.Should().Be("bot-2");
        result[1].Platform.Should().Be("telegram");
        result[1].NyxAgentApiKeyId.Should().Be("api-key-2");
        result[1].LegacyDirectBinding.Should().BeNull();
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
    public async Task BotRuntimeQueryPort_GetAsync_ComposesLegacyDirectBinding()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        documentReader.GetAsync("bot-runtime", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-runtime",
                Platform = "lark",
                NyxProviderSlug = "lark-provider",
                ScopeId = "scope-runtime",
                WebhookUrl = "https://example.com/relay",
                NyxChannelBotId = "channel-bot-runtime",
                NyxAgentApiKeyId = "api-key-runtime",
                NyxConversationRouteId = "route-runtime",
            }));

        var bindingReader = Substitute.For<IProjectionDocumentReader<ChannelBotLegacyDirectBindingDocument, string>>();
        bindingReader.GetAsync("bot-runtime", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotLegacyDirectBindingDocument?>(new ChannelBotLegacyDirectBindingDocument
            {
                Id = "bot-runtime",
                NyxUserToken = "access-runtime",
                NyxRefreshToken = "refresh-runtime",
                VerificationToken = "verify-runtime",
                CredentialRef = "secrets://runtime/bot-runtime",
                EncryptKey = "encrypt-runtime",
            }));

        var queryPort = new ChannelBotRegistrationRuntimeQueryPort(documentReader, bindingReader);
        var result = await queryPort.GetAsync("bot-runtime");

        result.Should().NotBeNull();
        result!.Id.Should().Be("bot-runtime");
        result.LegacyDirectBinding.Should().NotBeNull();
        result.NyxUserToken.Should().Be("access-runtime");
        result.NyxRefreshToken.Should().Be("refresh-runtime");
        result.VerificationToken.Should().Be("verify-runtime");
        result.CredentialRef.Should().Be("secrets://runtime/bot-runtime");
        result.EncryptKey.Should().Be("encrypt-runtime");
    }

    [Fact]
    public async Task BotRuntimeQueryPort_GetAsync_ReturnsPublicEntry_WhenLegacyBindingDocumentIsMissing()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        documentReader.GetAsync("bot-public-only", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-public-only",
                Platform = "lark",
                NyxConversationRouteId = "route-only",
            }));

        var bindingReader = Substitute.For<IProjectionDocumentReader<ChannelBotLegacyDirectBindingDocument, string>>();
        bindingReader.GetAsync("bot-public-only", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotLegacyDirectBindingDocument?>(null));

        var queryPort = new ChannelBotRegistrationRuntimeQueryPort(documentReader, bindingReader);
        var result = await queryPort.GetAsync("bot-public-only");

        result.Should().NotBeNull();
        result!.NyxConversationRouteId.Should().Be("route-only");
        result.LegacyDirectBinding.Should().BeNull();
        result.NyxUserToken.Should().BeEmpty();
        result.EncryptKey.Should().BeEmpty();
    }

    [Fact]
    public async Task BotRuntimeQueryPort_GetAsync_FallsBackToLegacyFieldsStoredOnRegistrationDocument()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        documentReader.GetAsync("bot-legacy-doc", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-legacy-doc",
                Platform = "lark",
                NyxUserToken = "access-from-doc",
                NyxRefreshToken = "refresh-from-doc",
                VerificationToken = "verify-from-doc",
                CredentialRef = "secrets://legacy/doc",
                EncryptKey = "encrypt-from-doc",
            }));

        var bindingReader = Substitute.For<IProjectionDocumentReader<ChannelBotLegacyDirectBindingDocument, string>>();
        bindingReader.GetAsync("bot-legacy-doc", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotLegacyDirectBindingDocument?>(null));

        var queryPort = new ChannelBotRegistrationRuntimeQueryPort(documentReader, bindingReader);
        var result = await queryPort.GetAsync("bot-legacy-doc");

        result.Should().NotBeNull();
        result!.LegacyDirectBinding.Should().NotBeNull();
        result.NyxUserToken.Should().Be("access-from-doc");
        result.NyxRefreshToken.Should().Be("refresh-from-doc");
        result.VerificationToken.Should().Be("verify-from-doc");
        result.CredentialRef.Should().Be("secrets://legacy/doc");
        result.EncryptKey.Should().Be("encrypt-from-doc");
    }

    [Fact]
    public async Task BotRuntimeQueryPort_GetAsync_ReturnsNull_WhenDocumentNotFound()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        documentReader.GetAsync("missing-runtime", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(null));
        var bindingReader = Substitute.For<IProjectionDocumentReader<ChannelBotLegacyDirectBindingDocument, string>>();

        var queryPort = new ChannelBotRegistrationRuntimeQueryPort(documentReader, bindingReader);
        var result = await queryPort.GetAsync("missing-runtime");

        result.Should().BeNull();
        await bindingReader.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(new UserAgentCatalogDocument
            {
                Id = "agent-1",
                Platform = "lark",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
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
        result.NyxApiKey.Should().BeEmpty();
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
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.GetAsync("agent-2", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(new UserAgentCatalogDocument
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
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items =
                [
                    new UserAgentCatalogDocument
                    {
                        Id = "agent-a",
                        Platform = "lark",
                        ConversationId = "oc_a",
                        AgentType = "skill_runner",
                        TemplateName = "daily_report",
                        StateVersion = 1,
                    },
                    new UserAgentCatalogDocument
                    {
                        Id = "agent-b",
                        Platform = "lark",
                        ConversationId = "oc_b",
                        Tombstoned = true,
                        StateVersion = 2,
                    },
                ],
            }));

        var queryPort = new UserAgentCatalogQueryPort(reader);
        var result = await queryPort.QueryAllAsync();

        result.Should().ContainSingle();
        result[0].AgentId.Should().Be("agent-a");
        result[0].NyxApiKey.Should().BeEmpty();
        result[0].AgentType.Should().Be("skill_runner");
        result[0].TemplateName.Should().Be("daily_report");
    }

    [Fact]
    public async Task UserAgentCatalogQueryPort_GetStateVersionAsync_ReturnsVersion_WhenDocumentExists()
    {
        var reader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        reader.GetAsync("agent-3", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(new UserAgentCatalogDocument
            {
                Id = "agent-3",
                StateVersion = 11,
            }));

        var queryPort = new UserAgentCatalogQueryPort(reader);
        var result = await queryPort.GetStateVersionAsync("agent-3");

        result.Should().Be(11);
    }

    [Fact]
    public async Task UserAgentCatalogRuntimeQueryPort_GetAsync_ComposesNyxCredential()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        documentReader.GetAsync("agent-runtime", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(new UserAgentCatalogDocument
            {
                Id = "agent-runtime",
                Platform = "lark",
                ConversationId = "oc_runtime",
                NyxProviderSlug = "api-lark-bot",
                OwnerNyxUserId = "user-runtime",
                AgentType = "skill_runner",
            }));

        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        credentialReader.GetAsync("agent-runtime", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogNyxCredentialDocument?>(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "agent-runtime",
                NyxApiKey = "nyx-key-runtime",
            }));

        var queryPort = new UserAgentCatalogRuntimeQueryPort(documentReader, credentialReader);
        var result = await queryPort.GetAsync("agent-runtime");

        result.Should().NotBeNull();
        result!.AgentId.Should().Be("agent-runtime");
        result.NyxApiKey.Should().Be("nyx-key-runtime");
        result.NyxProviderSlug.Should().Be("api-lark-bot");
    }

    [Fact]
    public async Task UserAgentCatalogRuntimeQueryPort_QueryAllAsync_ComposesNyxCredentials()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        documentReader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<UserAgentCatalogDocument>
            {
                Items =
                [
                    new UserAgentCatalogDocument
                    {
                        Id = "agent-a",
                        Platform = "lark",
                        ConversationId = "oc_a",
                        StateVersion = 1,
                    },
                    new UserAgentCatalogDocument
                    {
                        Id = "agent-b",
                        Platform = "lark",
                        ConversationId = "oc_b",
                        Tombstoned = true,
                        StateVersion = 2,
                    },
                ],
            }));

        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        credentialReader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<UserAgentCatalogNyxCredentialDocument>
            {
                Items =
                [
                    new UserAgentCatalogNyxCredentialDocument { Id = "agent-a", NyxApiKey = "key-a" },
                    new UserAgentCatalogNyxCredentialDocument { Id = "agent-b", NyxApiKey = "key-b" },
                ],
            }));

        var queryPort = new UserAgentCatalogRuntimeQueryPort(documentReader, credentialReader);
        var result = await queryPort.QueryAllAsync();

        result.Should().ContainSingle();
        result[0].AgentId.Should().Be("agent-a");
        result[0].NyxApiKey.Should().Be("key-a");
    }
}
