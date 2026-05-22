using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="AevatarOAuthClientBootstrapService"/>.
/// </summary>
[Collection(NyxIdRedirectUriEnvCollection.Name)]
public sealed class AevatarOAuthClientBootstrapServiceTests
{
    [Fact]
    public async Task EnsureProvisionedAsync_SkipsDispatch_WhenSnapshotAlreadyMatchesResolvedClient()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var dispatch = new RecordingDispatch();
        var service = NewService(
            new StaticClientProvider(Snapshot(
                authority: environment.Authority,
                redirectUri: environment.RedirectUri,
                oauthScope: AevatarOAuthClientScopes.AuthorizationScope)),
            dispatch);

        await service.EnsureProvisionedAsync(CancellationToken.None);

        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureProvisionedAsync_DispatchesAcceptedCommand_WhenSnapshotIsMissing()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var dispatch = new RecordingDispatch();
        var service = NewService(new MissingClientProvider(), dispatch);

        await service.EnsureProvisionedAsync(CancellationToken.None);

        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.NyxidAuthority.Should().Be(environment.Authority);
        command.RedirectUri.Should().Be(environment.RedirectUri);
        command.ClientName.Should().Be("aevatar");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_DispatchesAcceptedCommand_WhenSnapshotDrifted()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var dispatch = new RecordingDispatch();
        var service = NewService(
            new StaticClientProvider(Snapshot(
                authority: environment.Authority,
                redirectUri: "https://old.example.com/api/oauth/nyxid-callback",
                oauthScope: "openid")),
            dispatch);

        await service.EnsureProvisionedAsync(CancellationToken.None);

        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.NyxidAuthority.Should().Be(environment.Authority);
        command.RedirectUri.Should().Be(environment.RedirectUri);
        command.ClientName.Should().Be("aevatar");
    }

    [Fact]
    public async Task EnsureProvisionedAsync_Throws_WhenDispatchRejects()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var service = NewService(new MissingClientProvider(), new RejectingDispatch());

        var act = () => service.EnsureProvisionedAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*InvalidTarget*");
    }

    private static AevatarOAuthClientBootstrapService NewService(
        IAevatarOAuthClientProvider provider,
        ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> dispatch) =>
        new(provider, dispatch, NullLogger<AevatarOAuthClientBootstrapService>.Instance);

    private static AevatarOAuthClientSnapshot Snapshot(
        string authority,
        string redirectUri,
        string oauthScope) =>
        new(
            ClientId: "client-1",
            ClientIdIssuedAt: DateTimeOffset.FromUnixTimeSeconds(1700000000),
            HmacKid: AevatarOAuthClientGAgent.InitialHmacKid,
            HmacKey: new byte[32],
            HmacKeyRotatedAt: DateTimeOffset.UtcNow,
            NyxIdAuthority: authority,
            BrokerCapabilityObserved: true,
            BrokerCapabilityObservedAt: DateTimeOffset.UtcNow,
            RedirectUri: redirectUri,
            OauthScope: oauthScope);

    private sealed class OAuthBootstrapEnvironment : IDisposable
    {
        private readonly string? _oldAuthority;
        private readonly string? _oldRedirectBaseUrl;

        public string Authority { get; } = "https://nyxid.test";
        public string RedirectBaseUrl { get; } = "https://aevatar.test";
        public string RedirectUri => $"{RedirectBaseUrl}{NyxIdRedirectUriResolver.CallbackPath}";

        public OAuthBootstrapEnvironment()
        {
            _oldAuthority = Environment.GetEnvironmentVariable(NyxIdAuthorityResolver.OverrideEnvVar);
            _oldRedirectBaseUrl = Environment.GetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar);
            Environment.SetEnvironmentVariable(NyxIdAuthorityResolver.OverrideEnvVar, Authority);
            Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, RedirectBaseUrl);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(NyxIdAuthorityResolver.OverrideEnvVar, _oldAuthority);
            Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, _oldRedirectBaseUrl);
        }
    }

    private sealed class StaticClientProvider(AevatarOAuthClientSnapshot snapshot) : IAevatarOAuthClientProvider
    {
        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class MissingClientProvider : IAevatarOAuthClientProvider
    {
        public Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default) =>
            throw new AevatarOAuthClientNotProvisionedException();
    }

    private sealed class RecordingDispatch
        : ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
    {
        public List<EnsureAevatarOAuthClientProvisionedCommand> Commands { get; } = new();

        public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
            EnsureAevatarOAuthClientProvisionedCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Success(
                new ChannelIdentityOAuthAcceptedReceipt(
                    ActorId: AevatarOAuthClientGAgent.WellKnownId,
                    CommandId: "cmd-1",
                    CorrelationId: "cmd-1")));
        }
    }

    private sealed class RejectingDispatch
        : ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
    {
        public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(
            EnsureAevatarOAuthClientProvisionedCommand command,
            CancellationToken ct = default) =>
            Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Failure(
                ChannelIdentityOAuthDispatchError.InvalidTarget));
    }
}
