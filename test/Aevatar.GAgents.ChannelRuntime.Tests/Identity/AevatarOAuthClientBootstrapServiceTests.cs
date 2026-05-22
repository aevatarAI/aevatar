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
        var dispatch = new RecordingCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand>(
            static _ => OAuthClientReceipt());
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
        var dispatch = new RecordingCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand>(
            static _ => OAuthClientReceipt());
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
        var dispatch = new RecordingCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand>(
            static _ => OAuthClientReceipt());
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
        var service = NewService(
            new MissingClientProvider(),
            new RejectingCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand>());

        var act = () => service.EnsureProvisionedAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*InvalidTarget*");
    }

    [Fact]
    public void IdentityOAuthSource_ShouldNotContainProjectionReadinessOrPollingCompletionPath()
    {
        var endpointSource = RemoveRefactorSelfDocLines(File.ReadAllText(GetRepositoryPath(
            "agents",
            "Aevatar.GAgents.Channel.Identity",
            "Endpoints",
            "IdentityOAuthEndpoints.cs")));
        var bootstrapSource = RemoveRefactorSelfDocLines(ExtractEnsureProvisionedSource(File.ReadAllText(GetRepositoryPath(
            "agents",
            "Aevatar.GAgents.Channel.Identity",
            "Provisioning",
            "AevatarOAuthClientBootstrapService.cs"))));
        var combinedSource = string.Join(Environment.NewLine, endpointSource, bootstrapSource);

        combinedSource.Should().NotContain("IProjection" + "ReadinessPort");
        combinedSource.Should().NotContain("ExternalIdentityBinding" + "ProjectionPort");
        combinedSource.Should().NotContain("AevatarOAuthClient" + "ProjectionPort");
        combinedSource.Should().NotContain("AevatarOAuthClient" + "RebuildCoordinator");
        combinedSource.Should().NotContain("ProjectionWait" + "Timeout");
        combinedSource.Should().NotContain("WaitForRebuild" + "ObservedAsync");
        combinedSource.Should().NotContain("Rebuild" + "Observation");
        combinedSource.Should().NotContain("WaitForBinding" + "StateAsync");
        combinedSource.Should().NotContain(string.Concat("Task", ".Delay"));
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

    private static ChannelIdentityOAuthAcceptedReceipt OAuthClientReceipt() =>
        new(
            ActorId: AevatarOAuthClientGAgent.WellKnownId,
            CommandId: "cmd-1",
            CorrelationId: "cmd-1");

    private static string GetRepositoryPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)} from test output directory.");
    }

    private static string RemoveRefactorSelfDocLines(string source) =>
        string.Join(
            Environment.NewLine,
            source
                .Split('\n')
                .Where(static line =>
                    !line.Contains("Refactor (iter27/cluster-028-identity-oauth-endpoint)", StringComparison.Ordinal) &&
                    !line.Contains("Old pattern:", StringComparison.Ordinal) &&
                    !line.Contains("New principle:", StringComparison.Ordinal)));

    private static string ExtractEnsureProvisionedSource(string source)
    {
        const string marker = "internal async Task EnsureProvisionedAsync";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "bootstrap source should keep the dispatch completion method");
        return source[start..];
    }

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

}
