using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class RegistrationQueryPortTests
{
    private static Aevatar.Foundation.Abstractions.Credentials.SecretReference TestDeliverySecretReference(string registrationId) =>
        new()
        {
            Ref = $"sec_delivery_{registrationId}",
            Purpose = Aevatar.Foundation.Abstractions.Credentials.CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            OwnerScopeKey = "scope-x",
        };

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
                DeviceEventTargetActorId = "household-scope-a",
            }));

        var queryPort = new DeviceRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("reg-1");

        result.Should().NotBeNull();
        result!.Id.Should().Be("reg-1");
        result.ScopeId.Should().Be("scope-a");
        result.HmacKey.Should().Be("key-abc");
        result.NyxConversationId.Should().Be("conv-42");
        result.Description.Should().Be("Test device");
        result.DeviceEventTargetActorId.Should().Be("household-scope-a");
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
                WorkflowResultDeliveryCredential = TestDeliverySecretReference("bot-1"),
                WorkflowResultDeliveryRepair = FailedRepair(),
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
        result.WorkflowResultDeliveryCredential.Should().Be(TestDeliverySecretReference("bot-1"));
        result.WorkflowResultDeliveryRepair.Should().Be(FailedRepair());
        result.WorkflowResultDeliveryRepair.Should().NotBeSameAs(
            (await reader.GetAsync("bot-1", CancellationToken.None))!.WorkflowResultDeliveryRepair);
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

    [Fact]
    public async Task ConversationDeliveryQueryPort_GetAsync_ReadsCurrentStateDocumentByActorId()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ConversationDeliveryCurrentStateDocument, string>>();
        reader.GetAsync("conversation-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ConversationDeliveryCurrentStateDocument?>(new ConversationDeliveryCurrentStateDocument
            {
                Id = "conversation-1",
                ActorId = "conversation-1",
                LastSuccessfulDelivery = new DeliveryLedgerEntry
                {
                    DeliveryKind = DeliveryKind.TextMessage,
                    Status = DeliveryStatus.Succeeded,
                    Target = new DeliveryTarget
                    {
                        Channel = ChannelId.From("lark"),
                        ConversationKey = "lark:tenant:thread",
                    },
                    ProviderMessageId = "om_1",
                    RequestId = "request-1",
                },
            }));

        var queryPort = new ConversationDeliveryQueryPort(reader);
        var result = await queryPort.GetAsync(" conversation-1 ");

        result.Should().NotBeNull();
        result!.ActorId.Should().Be("conversation-1");
        result.LastSuccessfulDelivery.Should().NotBeNull();
        result.LastSuccessfulDelivery!.ProviderMessageId.Should().Be("om_1");
        await reader.Received(1).GetAsync("conversation-1", Arg.Any<CancellationToken>());
        await reader.DidNotReceive().QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConversationDeliveryQueryPort_GetAsync_BlankActorId_ReturnsNullWithoutRead()
    {
        var reader = Substitute.For<IProjectionDocumentReader<ConversationDeliveryCurrentStateDocument, string>>();
        var queryPort = new ConversationDeliveryQueryPort(reader);

        var result = await queryPort.GetAsync(" ");

        result.Should().BeNull();
        await reader.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static ChannelWorkflowResultDeliveryRepairState FailedRepair() =>
        new()
        {
            RequestId = "repair-1",
            Status = ChannelWorkflowResultDeliveryRepairStatus.Failed,
            ExpectedApiKeyId = "key-1",
            ExpectedConversationRouteId = "route-1",
            RotatedApiKeyId = "key-2",
            PreparedSecretReference = TestDeliverySecretReference("bot-1"),
            FailurePhase = ChannelWorkflowResultDeliveryRepairPhase.RouteRebinding,
            FailureReason = ChannelWorkflowResultDeliveryRepairFailureReason.RouteUpdateFailed,
            RequestedBySubjectId = "user-1",
            RequestedAtUnixMs = 1784563200000,
            UpdatedAtUnixMs = 1784563201000,
        };
}
