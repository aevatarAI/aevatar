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
                RuntimeConfig = TestRuntimeConfig(),
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
        result.RuntimeConfig.Should().Be(TestRuntimeConfig());
        result.RuntimeConfig.Should().NotBeSameAs(
            (await reader.GetAsync("bot-1", CancellationToken.None))!.RuntimeConfig);
        result.WorkflowResultDeliveryRepair.Should().Be(FailedRepair());
        result.WorkflowResultDeliveryRepair.Should().NotBeSameAs(
            (await reader.GetAsync("bot-1", CancellationToken.None))!.WorkflowResultDeliveryRepair);
    }

    [Fact]
    public async Task BotQueryPort_GetAsync_PreservesUnifiedAuthorizationContractPresence()
    {
        var credential = TestChannelAgentKey("bot-new");
        var document = new ChannelBotRegistrationDocument
        {
            Id = "bot-new",
            Platform = "telegram",
            ScopeId = "scope-x",
            NyxAgentApiKeyId = credential.ApiKeyId,
            WorkflowResultDeliveryCredential = credential.SecretReference.Clone(),
            ChannelAgentKey = credential,
            AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
        };
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-new", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(document));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("bot-new");

        result.Should().NotBeNull();
        result!.AuthorizationMode.Should().Be(ChannelRegistrationAuthorizationMode.NyxidDefault);
        result.ChannelAgentKey.Should().Be(credential);
        result.ChannelAgentKey.Should().NotBeSameAs(credential);
        result.ChannelAgentKey.Grant.HasAllowAllServices.Should().BeTrue();
        result.ChannelAgentKey.Grant.AllowAllServices.Should().BeFalse();
        result.ChannelAgentKey.Grant.HasAllowAllNodes.Should().BeTrue();
        result.ChannelAgentKey.Grant.AllowAllNodes.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BotQueryPort_GetAsync_PreservesExplicitAllowlistPresenceAndClonesFacts(
        bool includeBusinessService)
    {
        var credential = TestChannelAgentKey("bot-explicit");
        credential.Grant.ScopePlanDigest =
            "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var allowlist = new ChannelRegistrationServiceAllowlist();
        if (includeBusinessService)
            allowlist.ServiceIds.Add("svc-alpha");
        var document = new ChannelBotRegistrationDocument
        {
            Id = "bot-explicit",
            Platform = "telegram",
            ScopeId = "scope-x",
            StateVersion = 43,
            NyxAgentApiKeyId = credential.ApiKeyId,
            WorkflowResultDeliveryCredential = credential.SecretReference.Clone(),
            RegistrationServiceAllowlist = allowlist,
            ChannelAgentKey = credential,
            AuthorizationMode = ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist,
        };
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.GetAsync("bot-explicit", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChannelBotRegistrationDocument?>(document));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.GetAsync("bot-explicit");

        result.Should().NotBeNull();
        result!.AuthorizationMode.Should().Be(
            ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist);
        result.RegistrationServiceAllowlist.Should().NotBeNull();
        result.RegistrationServiceAllowlist.ServiceIds.Should().Equal(allowlist.ServiceIds);
        result.RegistrationServiceAllowlist.Should().NotBeSameAs(allowlist);
        result.ChannelAgentKey.Should().Be(credential);
        result.ChannelAgentKey.Should().NotBeSameAs(credential);
        result.ChannelAgentKey.Grant.ScopePlanDigest.Should().Be(credential.Grant.ScopePlanDigest);
        (await queryPort.GetStateVersionAsync("bot-explicit")).Should().Be(43);
        var snapshot = await queryPort.GetSnapshotAsync("bot-explicit");
        snapshot.Should().NotBeNull();
        snapshot!.StateVersion.Should().Be(43);
        snapshot.Registration.ChannelAgentKey.Should().Be(credential);
        snapshot.Registration.ChannelAgentKey.Should().NotBeSameAs(credential);
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
    public async Task BotQueryPort_QueryAllSnapshotsAsync_MapsEntryAndVersionFromSingleDocumentQuery()
    {
        var credential = TestChannelAgentKey("bot-new");
        var document = new ChannelBotRegistrationDocument
        {
            Id = "bot-new",
            Platform = "telegram",
            ScopeId = "scope-x",
            StateVersion = 27,
            NyxAgentApiKeyId = credential.ApiKeyId,
            ChannelAgentKey = credential,
            AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
        };
        var reader = Substitute.For<IProjectionDocumentReader<ChannelBotRegistrationDocument, string>>();
        reader.QueryAsync(Arg.Any<ProjectionDocumentQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<ChannelBotRegistrationDocument>
            {
                Items = [document],
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.QueryAllSnapshotsAsync();

        result.Should().ContainSingle();
        result[0].Registration.Id.Should().Be("bot-new");
        result[0].Registration.ChannelAgentKey.Should().Be(credential);
        result[0].Registration.ChannelAgentKey.Should().NotBeSameAs(credential);
        result[0].StateVersion.Should().Be(27);
        await reader.Received(1).QueryAsync(
            Arg.Is<ProjectionDocumentQuery>(query => query.Take == 1000),
            Arg.Any<CancellationToken>());
        await reader.DidNotReceive().GetAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
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
    public async Task BotQueryPort_ListSnapshotsByNyxAgentApiKeyIdAsync_PreservesDocumentStateVersion()
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
                        StateVersion = 23,
                    },
                ],
            }));

        var queryPort = new ChannelBotRegistrationQueryPort(reader);
        var result = await queryPort.ListSnapshotsByNyxAgentApiKeyIdAsync("key-1");

        result.Should().ContainSingle();
        result[0].Registration.Id.Should().Be("bot-1");
        result[0].StateVersion.Should().Be(23);
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Take.Should().Be(32);
        capturedQuery.Filters.Should().ContainSingle();
        capturedQuery.Filters[0].FieldPath.Should().Be(
            nameof(ChannelBotRegistrationDocument.NyxAgentApiKeyId));
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

    private static ChannelBotRuntimeConfig TestRuntimeConfig() =>
        new()
        {
            Instructions = "Use the booking rules before proposing times.",
            DefaultSkill = new ChannelBotRuntimeDefaultSkillConfig
            {
                Name = "booking-capacity",
                Version = "2.3",
            },
            ToolSetRefs = { "channel.reply.default" },
            ExtraToolNames = { "ask_user" },
            NyxidServiceSelectors =
            {
                new ChannelBotRuntimeNyxIdServiceSelector
                {
                    ServiceSlug = "api-google-workspace",
                    EndpointNames = { "calendar_create_event" },
                },
            },
            CredentialSourceMode = ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey,
            AgentKeyServiceRequirements = new ChannelBotRuntimeAgentKeyServiceRequirements
            {
                AllowedServiceSlugs = { "api-google-workspace" },
            },
        };

    private static ChannelAgentKeyCredential TestChannelAgentKey(string registrationId)
    {
        var reference = TestDeliverySecretReference(registrationId);
        reference.Version = 1;
        reference.Fingerprint = $"sha256:{registrationId}";
        reference.CreatedAtUnixMs = 1775822400000;
        return new ChannelAgentKeyCredential
        {
            ApiKeyId = $"key-{registrationId}",
            SecretReference = reference,
            Grant = new ChannelAgentKeyGrantSnapshot
            {
                AllowedServiceIds = { "svc-alpha", "svc-beta" },
                AllowedNodeIds = { "node-alpha" },
                AllowAllServices = false,
                AllowAllNodes = false,
            },
        };
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
