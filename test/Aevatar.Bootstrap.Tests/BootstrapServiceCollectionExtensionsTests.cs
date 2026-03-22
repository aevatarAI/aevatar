using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Bootstrap.Tests;

public class BootstrapServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAevatarBootstrap_ShouldRegisterRuntimeSecretsAndConnectorBuilders()
    {
        using var home = new TemporaryAevatarHomeScope();
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddAevatarBootstrap(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetService<IActorRuntime>().Should().NotBeNull();
        provider.GetService<IAevatarSecretsStore>().Should().NotBeNull();

        var connectorBuilders = provider.GetServices<IConnectorBuilder>().ToList();
        connectorBuilders.Should().ContainSingle(x => x.GetType() == typeof(HttpConnectorBuilder));
        connectorBuilders.Should().ContainSingle(x => x.GetType() == typeof(CliConnectorBuilder));
        connectorBuilders.Should().ContainSingle(x => x.GetType() == typeof(TelegramUserConnectorBuilder));
    }

    [Fact]
    public void AddAevatarBootstrap_WhenCalledTwice_ShouldNotDuplicateConnectorBuilders()
    {
        using var home = new TemporaryAevatarHomeScope();
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();

        services.AddAevatarBootstrap(configuration);
        services.AddAevatarBootstrap(configuration);
        using var provider = services.BuildServiceProvider();

        var connectorBuilders = provider.GetServices<IConnectorBuilder>().ToList();
        connectorBuilders.Should().ContainSingle(x => x.GetType() == typeof(HttpConnectorBuilder));
        connectorBuilders.Should().ContainSingle(x => x.GetType() == typeof(CliConnectorBuilder));
        connectorBuilders.Should().ContainSingle(x => x.GetType() == typeof(TelegramUserConnectorBuilder));
    }

    [Fact]
    public void AddAevatarDefaultHost_ByDefault_ShouldRegisterBootstrapHostedServices()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarDefaultHost();

        var hostedServices = builder.Services
            .Where(x => x.ServiceType == typeof(IHostedService))
            .Select(x => x.ImplementationType)
            .ToList();

        hostedServices.Should().Contain(typeof(ConnectorBootstrapHostedService));
    }

    [Fact]
    public void AddAevatarDefaultHost_ShouldLoadEnvironmentAppSettingsFromApplicationBaseDirectory()
    {
        using var home = new TemporaryAevatarHomeScope();
        var environmentName = $"BootstrapBaseDir{Guid.NewGuid():N}";
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, $"appsettings.{environmentName}.json");
        var contentRoot = Path.Combine(Path.GetTempPath(), $"aevatar-bootstrap-content-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(
            appSettingsPath,
            """
            {
              "BootstrapTest": {
                "Value": "loaded-from-app-base"
              }
            }
            """);

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environmentName,
                ContentRootPath = contentRoot,
            });

            builder.AddAevatarDefaultHost(options =>
            {
                options.EnableConnectorBootstrap = false;
                options.EnableCors = false;
            });

            builder.Configuration["BootstrapTest:Value"].Should().Be("loaded-from-app-base");
        }
        finally
        {
            if (File.Exists(appSettingsPath))
                File.Delete(appSettingsPath);
            if (Directory.Exists(contentRoot))
                Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void AddAevatarDefaultHost_ShouldApplyDefaultListenUrls_WhenNoExplicitAddressConfigured()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();

        builder.AddAevatarDefaultHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
            options.DefaultListenUrls = "http://localhost:5100";
        });

        builder.WebHost
            .GetSetting(WebHostDefaults.ServerUrlsKey)
            .Should()
            .Be("http://localhost:5100");
    }

    [Fact]
    public void AddAevatarDefaultHost_ShouldNotOverrideExplicitUrlsConfiguration()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHostDefaults.ServerUrlsKey] = "http://localhost:6200",
        });

        builder.AddAevatarDefaultHost(options =>
        {
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
            options.DefaultListenUrls = "http://localhost:5100";
        });

        builder.Configuration[WebHostDefaults.ServerUrlsKey].Should().Be("http://localhost:6200");
        builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey).Should().Be("http://localhost:6200");
    }

    [Fact]
    public void UseAevatarDefaultHost_WhenAutoMapCapabilitiesDisabled_ShouldOnlyMapRootHealthRoute()
    {
        using var home = new TemporaryAevatarHomeScope();
        var builder = CreateBuilder();
        builder.AddAevatarCapability(
            "dummy",
            static (_, _) => { },
            static app => app.MapGet("/dummy", () => Results.Ok()));
        builder.AddAevatarDefaultHost(options =>
        {
            options.AutoMapCapabilities = false;
            options.EnableConnectorBootstrap = false;
            options.EnableCors = false;
        });

        var app = builder.Build();
        app.UseAevatarDefaultHost();

        var routeBuilder = (IEndpointRouteBuilder)app;
        var routeEndpoints = routeBuilder.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();

        routeEndpoints.Should().Contain("/");
        routeEndpoints.Should().NotContain("/dummy");
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? values = null)
    {
        var builder = new ConfigurationBuilder();
        if (values != null)
            builder.AddInMemoryCollection(values);

        return builder.Build();
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var options = new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        };
        return WebApplication.CreateBuilder(options);
    }

    private sealed class TemporaryAevatarHomeScope : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        public TemporaryAevatarHomeScope()
        {
            _previous = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
            _path = Path.Combine(Path.GetTempPath(), $"aevatar-bootstrap-tests-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _path);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _previous);
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
    }
}
