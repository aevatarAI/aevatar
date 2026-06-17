using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
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

        documentReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument
            {
                Id = "agent-1",
                ConversationId = "oc_chat_1",
                NyxProviderSlug = "api-lark-bot",
                OutputFormat = SkillRunnerOutputFormat.Text,
            });
        credentialReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "agent-1",
                NyxApiKey = "live-key",
            });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader);

        var target = await reader.GetAsync("agent-1", CancellationToken.None);

        target.Should().NotBeNull();
        target!.NyxApiKey.Should().Be("live-key");
        target.ConversationId.Should().Be("oc_chat_1");
        target.OutputFormat.Should().Be(SkillRunnerOutputFormat.Text);
    }

    [Fact]
    public async Task GetAsync_ResolvesExplicitDeliveryTargetAlias_ForWorkflowDelivery()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();

        documentReader.GetAsync("aelf-twitter-approval", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument
            {
                Id = "aelf-twitter-approval",
                ConversationId = "oc_9f1b8d3835674963417954fad20f8a3c",
                NyxProviderSlug = "api-lark-bot-2",
                LarkReceiveId = "oc_9f1b8d3835674963417954fad20f8a3c",
                LarkReceiveIdType = "chat_id",
                TargetPlatform = "lark",
                AgentType = "delivery_target",
                TemplateName = "explicit_delivery_target",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            });
        credentialReader.GetAsync("aelf-twitter-approval", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "aelf-twitter-approval",
                NyxApiKey = "secret-created-key",
            });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader);

        var target = await reader.GetAsync("aelf-twitter-approval", CancellationToken.None);

        target.Should().NotBeNull();
        target!.AgentId.Should().Be("aelf-twitter-approval");
        target.Platform.Should().Be("lark");
        target.ConversationId.Should().Be("oc_9f1b8d3835674963417954fad20f8a3c");
        target.NyxProviderSlug.Should().Be("api-lark-bot-2");
        target.NyxApiKey.Should().Be("secret-created-key");
        target.LarkReceiveId.Should().Be("oc_9f1b8d3835674963417954fad20f8a3c");
        target.LarkReceiveIdType.Should().Be("chat_id");
        target.AgentType.Should().Be("delivery_target");
    }

    [Fact]
    public async Task GetAsync_UsesTargetPlatform_When_NotLark()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();

        documentReader.GetAsync("email-approval", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument
            {
                Id = "email-approval",
                TargetPlatform = "email",
                ConversationId = "approvals@example.com",
                NyxProviderSlug = "api-email-outbound",
                AgentType = "delivery_target",
                TemplateName = "explicit_delivery_target",
                OwnerScope = OwnerScope.ForNyxIdNative("user-1"),
            });
        credentialReader.GetAsync("email-approval", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument
            {
                Id = "email-approval",
                NyxApiKey = "secret-created-key",
            });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader);

        var target = await reader.GetAsync("email-approval", CancellationToken.None);

        target.Should().NotBeNull();
        target!.Platform.Should().Be("email");
        target.ConversationId.Should().Be("approvals@example.com");
        target.LarkReceiveId.Should().BeEmpty();
        target.LarkReceiveIdType.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_When_DocumentMissing()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        documentReader.GetAsync("missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UserAgentCatalogDocument?>(null));

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader);
        (await reader.GetAsync("missing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_When_DocumentTombstoned()
    {
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        documentReader.GetAsync("dead", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument { Id = "dead", Tombstoned = true });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader);
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

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader);
        (await reader.GetAsync("agent-1", CancellationToken.None)).Should().BeNull(
            "credential not yet projected → fail-closed; never construct a target with empty NyxApiKey");
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_When_CredentialNyxApiKeyIsBlank()
    {
        // Same fail-closed behavior when the credential document exists but the key
        // is blank (ghost record / partial projection). Issue #466 review.
        var documentReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogDocument, string>>();
        var credentialReader = Substitute.For<IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string>>();
        documentReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogDocument { Id = "agent-1", ConversationId = "oc_chat_1" });
        credentialReader.GetAsync("agent-1", Arg.Any<CancellationToken>())
            .Returns(new UserAgentCatalogNyxCredentialDocument { Id = "agent-1", NyxApiKey = "" });

        var reader = new UserAgentDeliveryTargetReader(documentReader, credentialReader);
        (await reader.GetAsync("agent-1", CancellationToken.None)).Should().BeNull(
            "credential document exists but key is blank → fail-closed");
    }
}
