using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class AevatarOAuthClientProjectionProviderTests
{
    [Fact]
    public async Task GetAsync_BackfillsRedirectUrisFromLegacyRedirectUri()
    {
        var document = ProvisionedDocument();
        document.RedirectUri = "https://backend.test/api/oauth/nyxid-callback";
        var provider = CreateProvider(document, new InMemorySecretVault());

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
        var provider = CreateProvider(document, new InMemorySecretVault());

        var snapshot = await provider.GetAsync();

        snapshot.RedirectUris.Should().Equal(
            "https://backend.test/api/oauth/nyxid-callback",
            "https://console.test/auth/callback");
    }

    [Fact]
    public async Task GetAsync_ResolvesRefBackedKey_FromVault()
    {
        // New-write shape: the document carries only a vault ref (no legacy
        // plaintext bytes); the provider must resolve the raw key material.
        var vault = new InMemorySecretVault();
        var keyBytes = FilledKey(0x11);
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.OAuthStateTokenHmacKey,
            AevatarOAuthClientGAgent.WellKnownId,
            AevatarOAuthClientGAgent.WellKnownId,
            Convert.ToBase64String(keyBytes),
            "test.seed"));

        var document = ProvisionedDocument();
        document.HmacKey = ByteString.Empty;
        document.HmacKeyRef = stored.Reference;
        var provider = CreateProvider(document, vault);

        var snapshot = await provider.GetAsync();

        snapshot.HmacKey.Should().Equal(keyBytes);
    }

    [Fact]
    public async Task GetAsync_FallsBackToLegacyPlaintextKey_WhenNoRef()
    {
        // Legacy dual-read: state persisted before the vault migration carries
        // the plaintext key in [hmac_key] with no ref; the provider must still
        // yield a working snapshot.
        var legacyKey = FilledKey(0x22);
        var document = ProvisionedDocument();
        document.HmacKey = ByteString.CopyFrom(legacyKey);
        document.HmacKeyRef.Should().BeNull("legacy documents carry no vault ref");
        var provider = CreateProvider(document, new InMemorySecretVault());

        var snapshot = await provider.GetAsync();

        snapshot.HmacKey.Should().Equal(legacyKey);
    }

    [Fact]
    public async Task GetAsync_ResolvesPreviousRefKey_ForRotationGraceWindow()
    {
        // Rotation grace window with ref-backed keys: both the current and the
        // demoted previous key are vault refs, and both must resolve so an
        // in-flight token signed with the prior key still verifies.
        var vault = new InMemorySecretVault();
        var currentKey = FilledKey(0x33);
        var previousKey = FilledKey(0x44);
        var currentRef = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.OAuthStateTokenHmacKey,
            AevatarOAuthClientGAgent.WellKnownId,
            AevatarOAuthClientGAgent.WellKnownId,
            Convert.ToBase64String(currentKey),
            "test.rotate-current"));
        var previousRef = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.OAuthStateTokenHmacKey,
            AevatarOAuthClientGAgent.WellKnownId,
            AevatarOAuthClientGAgent.WellKnownId,
            Convert.ToBase64String(previousKey),
            "test.rotate-previous"));

        var document = ProvisionedDocument();
        document.HmacKey = ByteString.Empty;
        document.HmacKeyRef = currentRef.Reference;
        document.HmacKid = "v2";
        document.PreviousHmacKeyRef = previousRef.Reference;
        document.PreviousHmacKid = "v1";
        document.PreviousHmacDemotedAtUnix = 1700000500;
        var provider = CreateProvider(document, vault);

        var snapshot = await provider.GetAsync();

        snapshot.HmacKey.Should().Equal(currentKey);
        snapshot.PreviousHmacKid.Should().Be("v1");
        snapshot.PreviousHmacKey.Should().Equal(previousKey);
        snapshot.PreviousHmacDemotedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000500));
    }

    [Fact]
    public async Task GetAsync_DropsPreviousKey_WhenPreviousRefCannotResolve_ButKeepsCurrent()
    {
        // Availability guard: a lost/unresolvable PREVIOUS (grace-window) key reference
        // must not fault the whole snapshot — current-key token verification stays
        // healthy and only the demoted key is dropped. Contrast with a dangling CURRENT
        // ref, which is fail-closed (GetAsync_Throws_WhenRefCannotResolve).
        var vault = new InMemorySecretVault();
        var currentKey = FilledKey(0x55);
        var currentRef = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.OAuthStateTokenHmacKey,
            AevatarOAuthClientGAgent.WellKnownId,
            AevatarOAuthClientGAgent.WellKnownId,
            Convert.ToBase64String(currentKey),
            "test.rotate-current"));

        var document = ProvisionedDocument();
        document.HmacKey = ByteString.Empty;
        document.HmacKeyRef = currentRef.Reference;
        document.HmacKid = "v2";
        // Previous ref points at a secret the vault never stored → unresolvable.
        document.PreviousHmacKeyRef = new SecretReference
        {
            Ref = "sec_0000000000000077",
            Purpose = CredentialSecretPurposes.OAuthStateTokenHmacKey,
            OwnerScopeKey = AevatarOAuthClientGAgent.WellKnownId,
        };
        document.PreviousHmacKid = "v1";
        document.PreviousHmacDemotedAtUnix = 1700000500;
        var provider = CreateProvider(document, vault);

        var snapshot = await provider.GetAsync();

        snapshot.HmacKey.Should().Equal(currentKey);
        snapshot.PreviousHmacKey.Should().BeNull();
        snapshot.PreviousHmacKid.Should().BeNull();
        snapshot.PreviousHmacDemotedAt.Should().BeNull();
    }

    [Fact]
    public async Task RefBackedSnapshot_RoundTripsThroughStateTokenCodec()
    {
        // End-to-end: a ref-backed document resolves via the vault into a
        // snapshot whose HMAC key the StateTokenCodec uses unchanged. The
        // token must encode + decode round-trip, proving the resolved bytes
        // are the same material the codec signs and verifies with.
        var vault = new InMemorySecretVault();
        var keyBytes = FilledKey(0x66);
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.OAuthStateTokenHmacKey,
            AevatarOAuthClientGAgent.WellKnownId,
            AevatarOAuthClientGAgent.WellKnownId,
            Convert.ToBase64String(keyBytes),
            "test.seed"));

        var document = ProvisionedDocument();
        document.HmacKey = ByteString.Empty;
        document.HmacKeyRef = stored.Reference;
        var provider = CreateProvider(document, vault);
        var codec = new StateTokenCodec(provider, options: null, timeProvider: null);

        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "ou_tenant_x",
            ExternalUserId = "ou_user_y",
        };
        var token = await codec.EncodeAsync("corr-round-trip", subject, "verifier-abc");
        var result = await codec.TryDecodeAsync(token);

        result.Succeeded.Should().BeTrue();
        result.Payload!.CorrelationId.Should().Be("corr-round-trip");
        result.Payload.PkceVerifier.Should().Be("verifier-abc");
        result.Payload.ExternalSubject.ExternalUserId.Should().Be("ou_user_y");
    }

    [Fact]
    public async Task GetAsync_Throws_WhenRefCannotResolve()
    {
        // A dangling ref (empty vault) is a provisioning fault, not a silent
        // fall-through to stale legacy bytes.
        var document = ProvisionedDocument();
        document.HmacKey = ByteString.Empty;
        document.HmacKeyRef = new SecretReference
        {
            Ref = "sec_0000000000000099",
            Purpose = CredentialSecretPurposes.OAuthStateTokenHmacKey,
            OwnerScopeKey = AevatarOAuthClientGAgent.WellKnownId,
        };
        var provider = CreateProvider(document, new InMemorySecretVault());

        await Assert.ThrowsAsync<AevatarOAuthClientNotProvisionedException>(() => provider.GetAsync());
    }

    [Fact]
    public async Task GetAsync_DoesNotCombineConfiguredClientWithStaleProjectedClient()
    {
        var document = ProvisionedDocument();
        document.ClientId = "stale-projected-client";
        var provider = CreateProvider(document, new InMemorySecretVault(), "configured-client");

        var act = () => provider.GetAsync();

        await act.Should()
            .ThrowAsync<AevatarOAuthClientNotProvisionedException>()
            .WithMessage("*has not been materialized*");
    }

    [Fact]
    public async Task GetAsync_DoesNotFallBackToProjectedClientId_WhenConfigurationIsMissing()
    {
        var document = ProvisionedDocument();
        var provider = CreateProvider(document, new InMemorySecretVault(), "  ");

        var act = () => provider.GetAsync();

        await act.Should()
            .ThrowAsync<AevatarOAuthClientNotProvisionedException>()
            .WithMessage($"*{AevatarOAuthClientOptions.ClientIdConfigurationKey}*");
    }

    private static byte[] FilledKey(byte value)
    {
        var key = new byte[32];
        Array.Fill(key, value);
        return key;
    }

    private static AevatarOAuthClientProjectionProvider CreateProvider(
        AevatarOAuthClientDocument document,
        ISecretVault vault,
        string clientId = "client-id") =>
        new(
            new StubReader(document),
            vault,
            Options.Create(new AevatarOAuthClientOptions { ClientId = clientId }));

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
