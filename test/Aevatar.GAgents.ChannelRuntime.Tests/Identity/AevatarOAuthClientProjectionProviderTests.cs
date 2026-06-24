using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using FluentAssertions;
using Google.Protobuf;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class AevatarOAuthClientProjectionProviderTests
{
    [Fact]
    public async Task GetAsync_BackfillsRedirectUrisFromLegacyRedirectUri()
    {
        var document = ProvisionedDocument();
        document.RedirectUri = "https://backend.test/api/oauth/nyxid-callback";
        var provider = new AevatarOAuthClientProjectionProvider(new StubReader(document));

        var snapshot = await provider.GetAsync();

        snapshot.RedirectUri.Should().Be("https://backend.test/api/oauth/nyxid-callback");
        snapshot.RedirectUris.Should().Equal("https://backend.test/api/oauth/nyxid-callback");
    }

    [Fact]
    public async Task GetAsync_PreservesRepeatedRedirectUrisWhenPresent()
    {
        var document = ProvisionedDocument();
        document.RedirectUri = "https://backend.test/api/oauth/nyxid-callback";
        document.RedirectUris.Add("https://backend.test/api/oauth/nyxid-callback");
        document.RedirectUris.Add("https://console.test/auth/callback");
        var provider = new AevatarOAuthClientProjectionProvider(new StubReader(document));

        var snapshot = await provider.GetAsync();

        snapshot.RedirectUris.Should().Equal(
            "https://backend.test/api/oauth/nyxid-callback",
            "https://console.test/auth/callback");
    }

    private static AevatarOAuthClientDocument ProvisionedDocument() => new()
    {
        Id = AevatarOAuthClientGAgent.WellKnownId,
        ClientId = "client-id",
        ClientIdIssuedAtUnix = 1700000000,
        HmacKey = ByteString.CopyFrom(new byte[32]),
        HmacKeyRotatedAtUnix = 1700000001,
        HmacKid = AevatarOAuthClientGAgent.InitialHmacKid,
        NyxidAuthority = "https://nyxid.test",
        OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
    };

    private sealed class StubReader : IProjectionDocumentReader<AevatarOAuthClientDocument, string>
    {
        private readonly AevatarOAuthClientDocument _document;

        public StubReader(AevatarOAuthClientDocument document)
        {
            _document = document;
        }

        public Task<AevatarOAuthClientDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<AevatarOAuthClientDocument?>(_document);

        public Task<ProjectionDocumentQueryResult<AevatarOAuthClientDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
