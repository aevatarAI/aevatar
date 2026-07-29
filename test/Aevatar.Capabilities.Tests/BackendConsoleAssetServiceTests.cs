using System.Reflection;
using Aevatar.BackendConsole.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Capabilities.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class BackendConsoleAssetServiceTests
{
    [Fact]
    public void Render_ShouldInjectConfiguredHostFacts()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:BackendConsole:OidcAuthority"] = "https://id.example.test",
                ["Aevatar:BackendConsole:OidcClientId"] = "client-example",
                ["Aevatar:BackendConsole:OidcScope"] = "openid profile",
                ["Aevatar:BackendConsole:OidcResources:0"] = "https://resource.example.test/custom",
                ["Aevatar:BackendConsole:OidcResources:1"] =
                    "https://id.example.test/api/v1/proxy/s/aevatar",
                ["Aevatar:BackendConsole:NyxApiBaseUrl"] = " https://api.example.test/// ",
                ["Aevatar:BackendConsole:StorageKey"] = "console:test",
                ["Aevatar:BackendConsole:DefaultReturnPath"] = "/admin",
            })
            .Build();
        services.AddBackendConsoleStaticAssets(configuration);

        var provider = services.BuildServiceProvider();
        var assets = provider.GetRequiredService<IBackendConsoleAssetService>();

        var html = assets.Render(new BackendConsoleAsset(
            "fixture",
            typeof(BackendConsoleAssetServiceTests).Assembly,
            "BackendConsoleAssetServiceTests.fixture.html",
            "text/html",
            InjectHostConfiguration: true));

        html.Should().Contain("\"authority\":\"https://id.example.test\"");
        html.Should().Contain("\"clientId\":\"client-example\"");
        html.Should().Contain(
            "\"resources\":[\"https://api.example.test/api/v1/proxy/s/aevatar\",\"https://api.example.test/api/v1/proxy/s/ornn-api\",\"https://resource.example.test/custom\"]");
        html.Should().Contain("\"nyxidApi\":\"https://api.example.test\"");
        html.Should().Contain("\"storageKey\":\"console:test\"");
        html.Should().NotContain("__BACKEND_CONSOLE_CONFIG__");
    }

    [Fact]
    public void Render_ShouldLetHostEnvironmentOverrideOptionalConfig()
    {
        using var authority = new EnvironmentVariableScope("HOST_BACKEND_CONSOLE_OIDC_AUTHORITY", "https://env-id.example.test");
        using var clientId = new EnvironmentVariableScope("HOST_BACKEND_CONSOLE_OIDC_CLIENT_ID", "env-client");
        using var scope = new EnvironmentVariableScope("HOST_BACKEND_CONSOLE_OIDC_SCOPE", "env-scope");
        using var api = new EnvironmentVariableScope("HOST_BACKEND_CONSOLE_NYX_API_BASE_URL", "https://env-api.example.test");
        using var storage = new EnvironmentVariableScope("HOST_BACKEND_CONSOLE_STORAGE_KEY", "env-storage");
        using var defaultReturn = new EnvironmentVariableScope("HOST_BACKEND_CONSOLE_DEFAULT_RETURN_PATH", "/voice");

        var services = new ServiceCollection();
        services.AddBackendConsoleStaticAssets(new ConfigurationBuilder().Build());

        var assets = services.BuildServiceProvider().GetRequiredService<IBackendConsoleAssetService>();

        var html = assets.Render(new BackendConsoleAsset(
            "fixture",
            typeof(BackendConsoleAssetServiceTests).Assembly,
            "BackendConsoleAssetServiceTests.fixture.html",
            "text/html",
            InjectHostConfiguration: true));

        html.Should().Contain("\"authority\":\"https://env-id.example.test\"");
        html.Should().Contain("\"clientId\":\"env-client\"");
        html.Should().Contain("\"scope\":\"env-scope\"");
        html.Should().Contain(
            "\"resources\":[\"https://env-api.example.test/api/v1/proxy/s/aevatar\",\"https://env-api.example.test/api/v1/proxy/s/ornn-api\"]");
        html.Should().Contain("\"nyxidApi\":\"https://env-api.example.test\"");
        html.Should().Contain("\"storageKey\":\"env-storage\"");
        html.Should().Contain("\"defaultReturnPath\":\"/voice\"");
    }

    [Fact]
    public void Render_ShouldUseNyxAndAuthFallbacksWhenBackendConsoleFieldsAreBlank()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:BackendConsole:OidcAuthority"] = "",
                ["Aevatar:BackendConsole:OidcClientId"] = "client-example",
                ["Aevatar:BackendConsole:OidcScope"] = "openid profile",
                ["Aevatar:BackendConsole:NyxApiBaseUrl"] = "",
                ["Aevatar:BackendConsole:StorageKey"] = "console:test",
                ["Aevatar:Authentication:Authority"] = "https://auth.example.test",
                ["Aevatar:NyxId:Authority"] = "https://nyx.example.test",
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyx-api.example.test",
            })
            .Build();
        services.AddBackendConsoleStaticAssets(configuration);

        var assets = services.BuildServiceProvider().GetRequiredService<IBackendConsoleAssetService>();

        var html = assets.Render(new BackendConsoleAsset(
            "fixture",
            typeof(BackendConsoleAssetServiceTests).Assembly,
            "BackendConsoleAssetServiceTests.fixture.html",
            "text/html",
            InjectHostConfiguration: true));

        html.Should().Contain("\"authority\":\"https://auth.example.test\"");
        html.Should().Contain("\"clientId\":\"client-example\"");
        html.Should().Contain("\"scope\":\"openid profile\"");
        html.Should().Contain(
            "\"resources\":[\"https://nyx-api.example.test/api/v1/proxy/s/aevatar\",\"https://nyx-api.example.test/api/v1/proxy/s/ornn-api\"]");
        html.Should().Contain("\"nyxidApi\":\"https://nyx-api.example.test\"");
        html.Should().Contain("\"storageKey\":\"console:test\"");
        html.Should().NotContain("__BACKEND_CONSOLE_CONFIG__");
    }

    [Fact]
    public void Render_ShouldRequestTheConfiguredOrnnProxyResource()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:BackendConsole:NyxApiBaseUrl"] = "https://api.example.test",
                ["Aevatar:Ornn:NyxIdSlug"] = "ornn-tenant-a",
            })
            .Build();
        services.AddBackendConsoleStaticAssets(configuration);

        var assets = services.BuildServiceProvider().GetRequiredService<IBackendConsoleAssetService>();
        var html = assets.Render(new BackendConsoleAsset(
            "fixture",
            typeof(BackendConsoleAssetServiceTests).Assembly,
            "BackendConsoleAssetServiceTests.fixture.html",
            "text/html",
            InjectHostConfiguration: true));

        html.Should().Contain(
            "\"resources\":[\"https://api.example.test/api/v1/proxy/s/aevatar\",\"https://api.example.test/api/v1/proxy/s/ornn-tenant-a\"]");
    }

    [Fact]
    public void Render_ShouldEncodeInjectedConfigForScriptContext()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:BackendConsole:OidcAuthority"] = "https://id.example.test</script><script>alert(1)</script>",
            })
            .Build();
        services.AddBackendConsoleStaticAssets(configuration);

        var assets = services.BuildServiceProvider().GetRequiredService<IBackendConsoleAssetService>();

        var html = assets.Render(new BackendConsoleAsset(
            "fixture",
            typeof(BackendConsoleAssetServiceTests).Assembly,
            "BackendConsoleAssetServiceTests.fixture.html",
            "text/html",
            InjectHostConfiguration: true));

        html.Should().Contain("\\u003C/script\\u003E");
        html.Should().NotContain("</script><script>alert(1)</script>");
    }

    [Fact]
    public void Render_ShouldFailWhenConfiguredAssetIsMissing()
    {
        var assets = BuildDefaultAssetService();

        var act = () => assets.Render(new BackendConsoleAsset(
            "missing",
            typeof(BackendConsoleAssetServiceTests).Assembly,
            "missing-fixture.html",
            "text/html",
            InjectHostConfiguration: true));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*missing-fixture.html*");
    }

    [Fact]
    public void Render_ShouldFailWhenInjectionPlaceholderIsMissing()
    {
        var assets = BuildDefaultAssetService();

        var act = () => assets.Render(new BackendConsoleAsset(
            "no-placeholder",
            typeof(BackendConsoleAssetServiceTests).Assembly,
            "BackendConsoleAssetServiceTests.no-placeholder.html",
            "text/html",
            InjectHostConfiguration: true));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no-placeholder*__BACKEND_CONSOLE_CONFIG__*");
    }

    private static IBackendConsoleAssetService BuildDefaultAssetService()
    {
        var services = new ServiceCollection();
        services.AddBackendConsoleStaticAssets(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider().GetRequiredService<IBackendConsoleAssetService>();
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }
}
