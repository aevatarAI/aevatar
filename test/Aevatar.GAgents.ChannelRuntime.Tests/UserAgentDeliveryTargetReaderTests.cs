using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentDeliveryTargetReaderTests
{
    [Fact]
    public async Task GetAsync_ReturnsTarget_When_DocumentAndCredentialMaterialized()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        var secretVault = new InMemorySecretVault();
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledNyxApiKey,
            "owner-scope:agent-1",
            "key-1",
            "live-key",
            "test"));

        documentReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument
            {
                Id = "agent-1",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
                ApiKeyId = "key-1",
                OutputFormat = ScheduledAgentOutputFormat.Text,
            });
        credentialReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "agent-1",
                ApiKeyId = "key-1",
                NyxApiKeyReference = stored.Reference,
            });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader, secretVault);

        var target = await reader.GetAsync("agent-1", CancellationToken.None);

        target.Should().NotBeNull();
        target!.NyxApiKey.Should().Be("live-key");
        target.ConversationId.Should().Be("oc_chat_1");
        target.OutputFormat.Should().Be(ScheduledAgentOutputFormat.Text);
    }

    [Fact]
    public async Task GetAsync_MapsLegacyLarkDocumentFields_ToChannelAddress()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        var secretVault = new InMemorySecretVault();
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledNyxApiKey,
            "owner-scope:agent-legacy-address",
            "key-legacy",
            "live-key",
            "test"));

        documentReader.GetAsync("agent-legacy-address", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument
            {
                Id = "agent-legacy-address",
                Platform = "lark",
                ConversationId = "oc_chat_legacy",
                NyxProviderSlug = "api-lark-bot",
                ApiKeyId = "key-legacy",
#pragma warning disable CS0612 // legacy fields simulate a document materialized before channel_address existed
                LarkReceiveId = "oc_dm_chat_1",
                LarkReceiveIdType = "chat_id",
                LarkReceiveIdFallback = "on_user_1",
                LarkReceiveIdTypeFallback = "union_id",
#pragma warning restore CS0612
            });
        credentialReader.GetAsync("agent-legacy-address", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "agent-legacy-address",
                ApiKeyId = "key-legacy",
                NyxApiKeyReference = stored.Reference,
            });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader, secretVault);

        var target = await reader.GetAsync("agent-legacy-address", CancellationToken.None);

        target.Should().NotBeNull();
        target!.ChannelAddress.Platform.Should().Be("lark");
        target.ChannelAddress.ProviderSlug.Should().Be("api-lark-bot");
        target.ChannelAddress.ConversationId.Should().Be("oc_chat_legacy");
        target.ChannelAddress.Primary.AddressId.Should().Be("oc_dm_chat_1");
        target.ChannelAddress.Primary.AddressType.Should().Be("chat_id");
        target.ChannelAddress.Fallback.Should().NotBeNull();
        target.ChannelAddress.Fallback!.AddressId.Should().Be("on_user_1");
        target.ChannelAddress.Fallback.AddressType.Should().Be("union_id");
    }

    [Fact]
    public async Task GetAsync_ReturnsTarget_When_CredentialReferenceUsesScheduledInvocationAgentKeyPurpose()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        var secretVault = new InMemorySecretVault();
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "owner-scope:scheduled-agent",
            "key-scheduled-agent",
            "scheduled-agent-key",
            "test"));

        documentReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument
            {
                Id = "agent-1",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
                ApiKeyId = "key-scheduled-agent",
                OutputFormat = ScheduledAgentOutputFormat.Text,
            });
        credentialReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "agent-1",
                ApiKeyId = "key-scheduled-agent",
                NyxApiKeyReference = stored.Reference,
            });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader, secretVault);

        var target = await reader.GetAsync("agent-1", CancellationToken.None);

        target.Should().NotBeNull();
        target!.NyxApiKey.Should().Be("scheduled-agent-key");
        target.ConversationId.Should().Be("oc_chat_1");
    }

    [Fact]
    public async Task GetAsync_ResolvesExplicitDeliveryTargetAlias_ForWorkflowDelivery()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        var secretVault = new InMemorySecretVault();
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledNyxApiKey,
            "owner-scope:aelf-twitter-approval",
            "key-approval",
            "secret-created-key",
            "test"));

        documentReader.GetAsync("aelf-twitter-approval", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument
            {
                Id = "aelf-twitter-approval",
                ConversationId = "oc_9f1b8d3835674963417954fad20f8a3c",
                NyxProviderSlug = "api-lark-bot-2",
                ChannelAddress = UserAgentCatalogChannelAddress.FromParts(
                    "lark",
                    "api-lark-bot-2",
                    "oc_9f1b8d3835674963417954fad20f8a3c",
                    "oc_9f1b8d3835674963417954fad20f8a3c",
                    "chat_id",
                    null,
                    null),
                TargetPlatform = "lark",
                AgentType = "delivery_target",
                TemplateName = "explicit_delivery_target",
                ApiKeyId = "key-approval",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            });
        credentialReader.GetAsync("aelf-twitter-approval", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "aelf-twitter-approval",
                ApiKeyId = "key-approval",
                NyxApiKeyReference = stored.Reference,
            });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader, secretVault);

        var target = await reader.GetAsync("aelf-twitter-approval", CancellationToken.None);

        target.Should().NotBeNull();
        target!.AgentId.Should().Be("aelf-twitter-approval");
        target.Platform.Should().Be("lark");
        target.ConversationId.Should().Be("oc_9f1b8d3835674963417954fad20f8a3c");
        target.NyxProviderSlug.Should().Be("api-lark-bot-2");
        target.NyxApiKey.Should().Be("secret-created-key");
        target.ChannelAddress.Platform.Should().Be("lark");
        target.ChannelAddress.Primary.AddressId.Should().Be("oc_9f1b8d3835674963417954fad20f8a3c");
        target.ChannelAddress.Primary.AddressType.Should().Be("chat_id");
        target.AgentType.Should().Be("delivery_target");
    }

    [Fact]
    public async Task GetAsync_UsesTargetPlatform_When_NotLark()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        var secretVault = new InMemorySecretVault();
        var stored = await secretVault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledNyxApiKey,
            "owner-scope:email-approval",
            "key-email",
            "secret-created-key",
            "test"));

        documentReader.GetAsync("email-approval", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument
            {
                Id = "email-approval",
                TargetPlatform = "email",
                ConversationId = "approvals@example.com",
                NyxProviderSlug = "api-email-outbound",
                ChannelAddress = UserAgentCatalogChannelAddress.FromParts(
                    "email",
                    "api-email-outbound",
                    "approvals@example.com",
                    "approvals@example.com",
                    string.Empty,
                    null,
                    null),
                AgentType = "delivery_target",
                TemplateName = "explicit_delivery_target",
                ApiKeyId = "key-email",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            });
        credentialReader.GetAsync("email-approval", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "email-approval",
                ApiKeyId = "key-email",
                NyxApiKeyReference = stored.Reference,
            });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader, secretVault);

        var target = await reader.GetAsync("email-approval", CancellationToken.None);

        target.Should().NotBeNull();
        target!.Platform.Should().Be("email");
        target.ConversationId.Should().Be("approvals@example.com");
        target.ChannelAddress.Platform.Should().Be("email");
        target.ChannelAddress.Primary.AddressId.Should().Be("approvals@example.com");
        target.ChannelAddress.Primary.AddressType.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_When_DocumentMissing()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        documentReader.GetAsync("missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(null));

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader, new InMemorySecretVault());
        (await reader.GetAsync("missing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_When_DocumentTombstoned()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        documentReader.GetAsync("dead", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument { Id = "dead", Tombstoned = true });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader, new InMemorySecretVault());
        (await reader.GetAsync("dead", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_When_CredentialDocumentMissing()
    {
        // Issue #466 review: when the credential document hasn't projected yet,
        // returning a delivery target with NyxApiKey="" would push the projection-lag
        // failure mode onto outbound Lark senders as a NyxID 401/403. The reader
        // fails closed instead — caller surfaces "delivery target unavailable".
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        documentReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument { Id = "agent-1", ConversationId = "oc_chat_1" });
        credentialReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogNyxCredentialDocument?>(null));

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader, new InMemorySecretVault());
        (await reader.GetAsync("agent-1", CancellationToken.None)).Should().BeNull(
            "credential not yet projected → fail-closed; never construct a target with empty NyxApiKey");
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_When_CredentialReferenceCannotResolve()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        documentReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument
            {
                Id = "agent-1",
                ConversationId = "oc_chat_1",
                ApiKeyId = "key-1",
            });
        credentialReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "agent-1",
                ApiKeyId = "key-1",
                NyxApiKeyReference = new SecretReference
                {
                    Ref = "missing",
                    Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                    OwnerScopeKey = "owner-scope:agent-1",
                },
            });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader, new InMemorySecretVault());
        (await reader.GetAsync("agent-1", CancellationToken.None)).Should().BeNull(
            "credential references must resolve before constructing a delivery target");
    }
}
