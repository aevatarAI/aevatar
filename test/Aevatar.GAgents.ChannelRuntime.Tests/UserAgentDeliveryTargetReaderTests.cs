using Aevatar.CQRS.Projection.Stores.Abstractions;
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
        // failure mode onto FeishuCardHumanInteractionPort as a NyxID 401/403. The
        // reader fails closed instead — caller surfaces "delivery target unavailable".
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
