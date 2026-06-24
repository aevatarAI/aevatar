using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity;
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
    public async Task StartAsync_DispatchesOneBootstrapIntent()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var dispatch = new RecordingCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        await service.StartAsync(CancellationToken.None);

        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.NyxidAuthority.Should().Be(environment.Authority);
        command.RedirectUri.Should().Be(environment.RedirectUri);
        command.RedirectUris.Should().Equal(environment.RedirectUri, environment.ConsoleRedirectUri);
        command.ClientName.Should().Be("aevatar");
        command.ForceReprovision.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_SetsForceReprovision_WhenBreakGlassEnvIsEnabled()
    {
        using var environment = new OAuthBootstrapEnvironment(forceDcrOnStartup: true);
        var dispatch = new RecordingCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        await service.StartAsync(CancellationToken.None);

        dispatch.Commands.Should().ContainSingle();
        dispatch.Commands[0].ForceReprovision.Should().BeTrue();
    }

    [Fact]
    public async Task DispatchBootstrapIntentAsync_DispatchesAcceptedCommand()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var dispatch = new RecordingCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        await service.DispatchBootstrapIntentAsync(CancellationToken.None);

        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.NyxidAuthority.Should().Be(environment.Authority);
        command.RedirectUri.Should().Be(environment.RedirectUri);
        command.RedirectUris.Should().Equal(environment.RedirectUri, environment.ConsoleRedirectUri);
        command.ClientName.Should().Be("aevatar");
        command.ForceReprovision.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchBootstrapIntentAsync_Throws_WhenDispatchRejects()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var service = NewService(new RejectingCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand>());

        var act = () => service.DispatchBootstrapIntentAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*InvalidTarget*");
    }

    [Fact]
    public async Task StopAsync_IsNoOp()
    {
        var service = NewService(new RejectingCommandDispatch<EnsureAevatarOAuthClientProvisionedCommand>());

        await service.StopAsync(CancellationToken.None);
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
        combinedSource.Should().NotContain(string.Concat("Task", ".Run"));
        bootstrapSource.Should().NotContain("IAevatarOAuthClient" + "Provider");
    }

    private static AevatarOAuthClientBootstrapService NewService(
        ICommandDispatchService<EnsureAevatarOAuthClientProvisionedCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> dispatch) =>
        new(dispatch, NullLogger<AevatarOAuthClientBootstrapService>.Instance);

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
        const string marker = "internal async Task DispatchBootstrapIntentAsync";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "bootstrap source should keep the dispatch completion method");
        return source[start..];
    }

    private sealed class OAuthBootstrapEnvironment : IDisposable
    {
        private readonly string? _oldAuthority;
        private readonly string? _oldRedirectBaseUrl;
        private readonly string? _oldAdditionalRedirectUris;
        private readonly string? _oldForceDcrOnStartup;

        public string Authority { get; } = "https://nyxid.test";
        public string RedirectBaseUrl { get; } = "https://aevatar.test";
        public string RedirectUri => $"{RedirectBaseUrl}{NyxIdRedirectUriResolver.CallbackPath}";
        public string ConsoleRedirectUri { get; } = "https://console.test/auth/callback";

        public OAuthBootstrapEnvironment(bool forceDcrOnStartup = false)
        {
            _oldAuthority = Environment.GetEnvironmentVariable(NyxIdAuthorityResolver.OverrideEnvVar);
            _oldRedirectBaseUrl = Environment.GetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar);
            _oldAdditionalRedirectUris = Environment.GetEnvironmentVariable(NyxIdRedirectUriResolver.AdditionalRedirectUrisEnvVar);
            _oldForceDcrOnStartup = Environment.GetEnvironmentVariable(AevatarOAuthClientBootstrapService.ForceDcrOnStartupEnvVar);
            Environment.SetEnvironmentVariable(NyxIdAuthorityResolver.OverrideEnvVar, Authority);
            Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, RedirectBaseUrl);
            Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.AdditionalRedirectUrisEnvVar, ConsoleRedirectUri);
            Environment.SetEnvironmentVariable(
                AevatarOAuthClientBootstrapService.ForceDcrOnStartupEnvVar,
                forceDcrOnStartup ? "true" : null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(NyxIdAuthorityResolver.OverrideEnvVar, _oldAuthority);
            Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, _oldRedirectBaseUrl);
            Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.AdditionalRedirectUrisEnvVar, _oldAdditionalRedirectUris);
            Environment.SetEnvironmentVariable(AevatarOAuthClientBootstrapService.ForceDcrOnStartupEnvVar, _oldForceDcrOnStartup);
        }
    }

}
