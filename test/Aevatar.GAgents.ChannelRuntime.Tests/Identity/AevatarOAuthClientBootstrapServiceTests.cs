using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="AevatarOAuthClientBootstrapService"/>.
/// </summary>
[Collection(NyxIdRedirectUriEnvCollection.Name)]
public sealed class AevatarOAuthClientBootstrapServiceTests
{
    private const string ConfiguredClientId = "configured-client-id";

    [Fact]
    public async Task StartAsync_WhenDisabled_ShouldNotDispatchBootstrapIntent()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch, enabled: false, clientId: string.Empty);

        await service.StartAsync(CancellationToken.None);

        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_DispatchesConfiguredClientProvisioning()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        await service.StartAsync(CancellationToken.None);

        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.ClientId.Should().Be(ConfiguredClientId);
        command.ClientIdIssuedAtUnix.Should().BeGreaterThan(0);
        command.NyxidAuthority.Should().Be(environment.Authority);
        command.RedirectUri.Should().Be(environment.RedirectUri);
        command.RedirectUris.Should().Equal(environment.RedirectUri, environment.ConsoleRedirectUri);
        command.OauthScope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
    }

    [Fact]
    public async Task StartAsync_Throws_WhenConfiguredClientIdIsMissing()
    {
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch, clientId: "  ");

        var act = () => service.StartAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{AevatarOAuthClientOptions.ClientIdConfigurationKey}*");
        dispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchBootstrapIntentAsync_DispatchesAcceptedCommand()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        await service.DispatchBootstrapIntentAsync(CancellationToken.None);

        dispatch.Commands.Should().ContainSingle();
        var command = dispatch.Commands[0];
        command.ClientId.Should().Be(ConfiguredClientId);
        command.NyxidAuthority.Should().Be(environment.Authority);
        command.RedirectUri.Should().Be(environment.RedirectUri);
        command.RedirectUris.Should().Equal(environment.RedirectUri, environment.ConsoleRedirectUri);
        command.OauthScope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
    }

    [Fact]
    public async Task DispatchBootstrapIntentAsync_Throws_WhenDispatchRejects()
    {
        using var environment = new OAuthBootstrapEnvironment();
        var service = NewService(new RejectingCommandDispatch<ProvisionAevatarOAuthClientCommand>());

        var act = () => service.DispatchBootstrapIntentAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*InvalidTarget*");
    }

    [Fact]
    public async Task DispatchBootstrapIntentAsync_BlocksPersistentLocal_WhenAuthorityFallsBackToProduction()
    {
        using var environment = new OAuthBootstrapEnvironment(
            environmentName: "PersistentLocal",
            configureAuthority: false);
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        var act = () => service.DispatchBootstrapIntentAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*PersistentLocal*{NyxIdAuthorityResolver.OverrideEnvVar}*configured NyxID OAuth client*");
        dispatch.Commands.Should().BeEmpty("bootstrap must fail before the actor can activate a client against production NyxID");
    }

    [Fact]
    public async Task DispatchBootstrapIntentAsync_BlocksPersistentLocal_WhenRedirectBaseUrlFallsBackToProduction()
    {
        using var environment = new OAuthBootstrapEnvironment(
            environmentName: "PersistentLocal",
            configureRedirectBaseUrl: false);
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        var act = () => service.DispatchBootstrapIntentAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*PersistentLocal*{NyxIdRedirectUriResolver.OverrideEnvVar}*configured NyxID OAuth client*");
        dispatch.Commands.Should().BeEmpty("bootstrap must fail before the production callback can be activated locally");
    }

    [Fact]
    public async Task DispatchBootstrapIntentAsync_BlocksPersistentLocal_WhenRedirectOverrideIsWildcard()
    {
        using var environment = new OAuthBootstrapEnvironment(
            environmentName: "PersistentLocal",
            redirectBaseUrl: "http://+:8080");
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        var act = () => service.DispatchBootstrapIntentAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*PersistentLocal*{NyxIdRedirectUriResolver.OverrideEnvVar}*configured NyxID OAuth client*");
        dispatch.Commands.Should().BeEmpty("wildcard listen addresses resolve to production default and must fail closed locally");
    }

    [Fact]
    public async Task DispatchBootstrapIntentAsync_BlocksDistributed_WhenProductionDefaultsWouldBeUsed()
    {
        using var environment = new OAuthBootstrapEnvironment(
            environmentName: "Distributed",
            configureAuthority: false,
            configureRedirectBaseUrl: false,
            configureAdditionalRedirectUris: false);
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        var act = () => service.DispatchBootstrapIntentAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage($"*Distributed*{NyxIdAuthorityResolver.OverrideEnvVar}*{NyxIdRedirectUriResolver.OverrideEnvVar}*configured NyxID OAuth client*");
        dispatch.Commands.Should().BeEmpty("Distributed is a runtime profile, not proof that production NyxID defaults are safe");
    }

    [Fact]
    public async Task DispatchBootstrapIntentAsync_AllowsUnsetEnvironmentProductionDefaults()
    {
        using var environment = new OAuthBootstrapEnvironment(
            environmentName: null,
            configureAuthority: false,
            configureRedirectBaseUrl: false,
            configureAdditionalRedirectUris: false);
        var dispatch = new RecordingCommandDispatch<ProvisionAevatarOAuthClientCommand>(
            static _ => OAuthClientReceipt());
        var service = NewService(dispatch);

        await service.DispatchBootstrapIntentAsync(CancellationToken.None);

        dispatch.Commands.Should().ContainSingle();
        dispatch.Commands[0].NyxidAuthority.Should().Be(NyxIdAuthorityResolver.DefaultAuthority);
        dispatch.Commands[0].RedirectUri.Should().Be(
            $"{NyxIdRedirectUriResolver.DefaultPublicBaseUrl}{NyxIdRedirectUriResolver.CallbackPath}");
    }

    [Fact]
    public async Task StopAsync_IsNoOp()
    {
        var service = NewService(new RejectingCommandDispatch<ProvisionAevatarOAuthClientCommand>());

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void AddChannelIdentity_MapsBackendConsoleClientIdIntoBootstrapOptions()
    {
        var values = new Dictionary<string, string?>
        {
            [AevatarOAuthClientOptions.ClientIdConfigurationKey] = "  configured-from-file  ",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();

        services.AddChannelIdentity(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<AevatarOAuthClientOptions>>()
            .Value.ClientId.Should().Be("configured-from-file");
    }

    [Fact]
    public void IdentityOAuthSource_ShouldNotContainProjectionReadinessOrPollingCompletionPath()
    {
        var endpointSource = RemoveRefactorSelfDocLines(File.ReadAllText(GetRepositoryPath(
            "agents",
            "Aevatar.GAgents.Channel.Identity",
            "Endpoints",
            "IdentityOAuthEndpoints.cs")));
        var bootstrapSource = RemoveRefactorSelfDocLines(ExtractBootstrapDispatchSource(File.ReadAllText(GetRepositoryPath(
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
        ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> dispatch,
        bool enabled = true,
        string clientId = ConfiguredClientId) =>
        new(
            dispatch,
            Options.Create(new AevatarOAuthClientOptions { ClientId = clientId }),
            Options.Create(new AevatarOAuthClientBootstrapOptions { Enabled = enabled }),
            NullLogger<AevatarOAuthClientBootstrapService>.Instance);

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

    private static string ExtractBootstrapDispatchSource(string source)
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
        private readonly string? _oldAspNetCoreEnvironment;
        private readonly string? _oldDotNetEnvironment;

        public string Authority { get; }
        public string RedirectBaseUrl { get; }
        public string RedirectUri => $"{RedirectBaseUrl}{NyxIdRedirectUriResolver.CallbackPath}";
        public string ConsoleRedirectUri { get; } = "https://console.test/auth/callback";

        public OAuthBootstrapEnvironment(
            string? environmentName = "PersistentLocal",
            bool configureAuthority = true,
            bool configureRedirectBaseUrl = true,
            bool configureAdditionalRedirectUris = true,
            string authority = "https://nyxid.test",
            string redirectBaseUrl = "https://aevatar.test")
        {
            Authority = authority;
            RedirectBaseUrl = redirectBaseUrl;
            _oldAuthority = Environment.GetEnvironmentVariable(NyxIdAuthorityResolver.OverrideEnvVar);
            _oldRedirectBaseUrl = Environment.GetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar);
            _oldAdditionalRedirectUris = Environment.GetEnvironmentVariable(NyxIdRedirectUriResolver.AdditionalRedirectUrisEnvVar);
            _oldAspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            _oldDotNetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environmentName);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
            Environment.SetEnvironmentVariable(
                NyxIdAuthorityResolver.OverrideEnvVar,
                configureAuthority ? Authority : null);
            Environment.SetEnvironmentVariable(
                NyxIdRedirectUriResolver.OverrideEnvVar,
                configureRedirectBaseUrl ? RedirectBaseUrl : null);
            Environment.SetEnvironmentVariable(
                NyxIdRedirectUriResolver.AdditionalRedirectUrisEnvVar,
                configureAdditionalRedirectUris ? ConsoleRedirectUri : null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(NyxIdAuthorityResolver.OverrideEnvVar, _oldAuthority);
            Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.OverrideEnvVar, _oldRedirectBaseUrl);
            Environment.SetEnvironmentVariable(NyxIdRedirectUriResolver.AdditionalRedirectUrisEnvVar, _oldAdditionalRedirectUris);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _oldAspNetCoreEnvironment);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _oldDotNetEnvironment);
        }
    }
}
