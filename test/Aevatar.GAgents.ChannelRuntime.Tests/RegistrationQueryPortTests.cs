using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class RegistrationQueryPortTests
{
    [Fact]
    public async Task DeviceQueryPort_GetAsync_ReturnsMappedEntry()
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
    public async Task BotQueryPort_GetAsync_ReturnsMappedPublicEntry()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(new ChannelBotRegistrationDocument
            {
                Id = "bot-1",
                Platform = "lark",
                NyxProviderSlug = "api-lark-bot",
                ScopeId = "scope-x",
                WebhookUrl = "https://example.com/callback/bot-1",
                NyxChannelBotId = "nyx-bot-1",
                NyxAgentApiKeyId = "key-1",
                NyxConversationRouteId = "route-1",
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("bot-1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("bot-1");
        result.Platform.Should().Be("lark");
        result.NyxProviderSlug.Should().Be("api-lark-bot");
        result.ScopeId.Should().Be("scope-x");
        result.WebhookUrl.Should().Be("https://example.com/callback/bot-1");
        result.NyxChannelBotId.Should().Be("nyx-bot-1");
        result.NyxAgentApiKeyId.Should().Be("key-1");
        result.NyxConversationRouteId.Should().Be("route-1");
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
                        NyxProviderSlug = "api-lark-bot",
                    },
                    new ChannelBotRegistrationDocument
                    {
                        Id = "bot-2",
                        Platform = "lark",
                        NyxProviderSlug = "api-lark-bot",
                    },
                ],
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.QueryAllAsync();

        result.Select(static entry => entry.Id).Should().Equal("bot-1", "bot-2");
    }

    [Fact]
    public async Task BotQueryPort_GetByNyxAgentApiKeyIdAsync_QueriesProjectionByIdentityField()
    {
        ProjectionDocumentQuery? capturedQuery = null;
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.QueryAsync(
                Arg.Do<ProjectionDocumentQuery>(query => capturedQuery = query),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<ChannelBotRegistrationDocument>
            {
                Items =
                [
                    new ChannelBotRegistrationDocument
                    {
                        Id = "bot-1",
                        Platform = "lark",
                        NyxAgentApiKeyId = "key-1",
                    },
                ],
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetByNyxAgentApiKeyIdAsync("key-1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("bot-1");
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Take.Should().Be(1);
        capturedQuery.Filters.Should().ContainSingle();
        capturedQuery.Filters[0].FieldPath.Should().Be(nameof(ChannelBotRegistrationDocument.NyxAgentApiKeyId));
        capturedQuery.Filters[0].Operator.Should().Be(ProjectionDocumentFilterOperator.Eq);
        capturedQuery.Filters[0].Value.RawValue.Should().Be("key-1");
    }

    [Fact]
    public async Task BotQueryPort_GetByNyxChannelBotIdAsync_QueriesProjectionByIdentityField()
    {
        ProjectionDocumentQuery? capturedQuery = null;
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.QueryAsync(
                Arg.Do<ProjectionDocumentQuery>(query => capturedQuery = query),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<ChannelBotRegistrationDocument>
            {
                Items =
                [
                    new ChannelBotRegistrationDocument
                    {
                        Id = "bot-2",
                        Platform = "lark",
                        NyxChannelBotId = "nyx-bot-2",
                    },
                ],
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetByNyxChannelBotIdAsync("nyx-bot-2");

        result.Should().NotBeNull();
        result!.Id.Should().Be("bot-2");
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Take.Should().Be(1);
        capturedQuery.Filters.Should().ContainSingle();
        capturedQuery.Filters[0].FieldPath.Should().Be(nameof(ChannelBotRegistrationDocument.NyxChannelBotId));
        capturedQuery.Filters[0].Operator.Should().Be(ProjectionDocumentFilterOperator.Eq);
        capturedQuery.Filters[0].Value.RawValue.Should().Be("nyx-bot-2");
    }

    [Fact]
    public async Task BotRuntimeQueryPort_DelegatesToPublicQueryPort()
    {
        var publicQueryPort = Substitute.For<IChannelBotRegistrationQueryPort>();
        publicQueryPort.GetAsync("bot-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationEntry?>(new ChannelBotRegistrationEntry
            {
                Id = "bot-1",
                Platform = "lark",
            }));

        var runtimeQueryPort = new ChannelBotRegistrationRuntimeQueryPort(publicQueryPort);
        var result = await runtimeQueryPort.GetAsync("bot-1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("bot-1");
        await publicQueryPort.Received(1).GetAsync("bot-1", Arg.Any<CancellationToken>());
    }
}
