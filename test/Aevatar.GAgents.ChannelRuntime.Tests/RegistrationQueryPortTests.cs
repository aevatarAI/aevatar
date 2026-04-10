using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
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
    public async Task DeviceQueryPort_ListAsync_ReturnsMappedEntries()
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
        var result = await queryPort.ListAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("reg-1");
        result[1].Id.Should().Be("reg-2");
    }

    [Fact]
    public async Task DeviceQueryPort_ListAsync_ReturnsEmpty_WhenNoDocuments()
    {
        var reader = Substitute.For<IProjectionDocumentReader<DeviceRegistrationDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ProjectionDocumentQueryResult<DeviceRegistrationDocument>.Empty));

        var queryPort = new DeviceRegistrationQueryPort(reader);
        var result = await queryPort.ListAsync();

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
    public async Task BotQueryPort_ListAsync_ReturnsMappedEntries()
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
        var result = await queryPort.ListAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("bot-1");
        result[0].Platform.Should().Be("lark");
        result[1].Id.Should().Be("bot-2");
        result[1].Platform.Should().Be("telegram");
    }

    [Fact]
    public async Task BotQueryPort_ListAsync_ReturnsEmpty_WhenNoDocuments()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ProjectionDocumentQueryResult<ChannelBotRegistrationDocument>.Empty));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.ListAsync();

        result.Should().BeEmpty();
    }
}
