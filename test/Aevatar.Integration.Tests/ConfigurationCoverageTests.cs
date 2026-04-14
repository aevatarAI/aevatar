using System.Security.Cryptography;
using Aevatar.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Integration.Tests;

public sealed class ConfigurationCoverageTests
{
    [Fact]
    public void AevatarPaths_ShouldRespectEnvironmentOverrides_AndBuildChildPaths()
    {
        var homeSuffix = $"aevatar-paths-{Guid.NewGuid():N}";
        var secretsPath = Path.Combine("%TEMP%", $"aevatar-secrets-{Guid.NewGuid():N}.json");

        using var homeScope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, $"~/{homeSuffix}");
        using var secretsScope = new EnvironmentVariableScope(AevatarPaths.SecretsPathEnv, secretsPath);

        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            homeSuffix);

        AevatarPaths.Root.Should().Be(expectedRoot);
        AevatarPaths.SecretsJson.Should().Be(Environment.ExpandEnvironmentVariables(secretsPath));
        AevatarPaths.ConfigJson.Should().Be(Path.Combine(expectedRoot, "config.json"));
        AevatarPaths.MCPJson.Should().Be(Path.Combine(expectedRoot, "mcp.json"));
        AevatarPaths.ConnectorsJson.Should().Be(Path.Combine(expectedRoot, "connectors.json"));
        AevatarPaths.AgentYaml("writer").Should().Be(Path.Combine(expectedRoot, "agents", "writer.yaml"));
        AevatarPaths.WorkflowYaml("pipeline").Should().Be(Path.Combine(expectedRoot, "workflows", "pipeline.yaml"));
    }

    [Fact]
    public void AevatarPaths_EnsureDirectories_ShouldCreateExpectedFolders()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"aevatar-paths-home-{Guid.NewGuid():N}");
        using var homeScope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, tempHome);

        AevatarPaths.EnsureDirectories();

        Directory.Exists(AevatarPaths.Root).Should().BeTrue();
        Directory.Exists(AevatarPaths.Agents).Should().BeTrue();
        Directory.Exists(AevatarPaths.Workflows).Should().BeTrue();
        Directory.Exists(AevatarPaths.Skills).Should().BeTrue();
        Directory.Exists(AevatarPaths.Tools).Should().BeTrue();
        Directory.Exists(AevatarPaths.Sessions).Should().BeTrue();
        Directory.Exists(AevatarPaths.Logs).Should().BeTrue();
        Directory.Exists(AevatarPaths.MCP).Should().BeTrue();

        if (Directory.Exists(tempHome))
            Directory.Delete(tempHome, recursive: true);
    }

    [Fact]
    public void AevatarPaths_RepoRoot_ShouldResolveCurrentRepository()
    {
        var repoRoot = AevatarPaths.RepoRoot;

        File.Exists(Path.Combine(repoRoot, "aevatar.slnx")).Should().BeTrue();
        AevatarPaths.RepoRootWorkflows.Should().Be(Path.Combine(repoRoot, "workflows"));
    }

    [Fact]
    public void AevatarSecretsStore_WithMasterKeyFile_ShouldSaveAndLoadEncrypted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-secrets-encrypted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "secrets.json");
        var keyPath = Path.Combine(dir, "masterkey.bin");
        File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));

        try
        {
            var store = new AevatarSecretsStore(path);
            store.Set("LLMProviders:Providers:deepseek:ApiKey", "secret-key");

            var text = File.ReadAllText(path);
            text.Should().Contain("\"schemaVersion\"");
            text.Should().Contain("\"AES-256-GCM\"");
            text.Should().NotContain("secret-key");

            var reloaded = new AevatarSecretsStore(path);
            reloaded.GetApiKey("deepseek").Should().Be("secret-key");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AevatarSecretsStore_WithInvalidEncryptedEnvelope_ShouldFallbackToEmptyThenWritable()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-secrets-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "secrets.json");

        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "algorithm": "AES-256-GCM",
              "nonceB64": "invalid",
              "tagB64": "invalid",
              "ciphertextB64": "invalid"
            }
            """);

        try
        {
            var store = new AevatarSecretsStore(path);
            store.GetAll().Should().BeEmpty();

            store.Set("After", "Write");

            var reloaded = new AevatarSecretsStore(path);
            reloaded.Get("After").Should().Be("Write");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ListenUrlResolver_ResolveListenUrls_PrioritizesExplicitBeforeConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Service:ListenUrls"] = "http://configured.local:1001",
            })
            .Build();

        using var aspnetScope = new EnvironmentVariableScope("ASPNETCORE_URLS", "http://env.local:1002");

        var resolved = ListenUrlResolver.ResolveListenUrls(
            "http://explicit.local:1000",
            config,
            "Service:ListenUrls",
            defaultPort: 5000);

        resolved.Should().Be("http://explicit.local:1000");
    }

    [Fact]
    public void ListenUrlResolver_ResolveListenUrls_UsesConfigurationThenAspNetThenEnvironmentThenDefault()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Service:ListenUrls"] = "http://configured.local:1001",
            })
            .Build();

        using var envScope = new EnvironmentVariableScope("ASPNETCORE_URLS", "http://env.local:1002");

        var resolvedFromConfiguration = ListenUrlResolver.ResolveListenUrls(
            null,
            config,
            "Service:ListenUrls",
            defaultPort: 5000);

        resolvedFromConfiguration.Should().Be("http://configured.local:1001");

        var blankConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var resolvedFromAspNet = ListenUrlResolver.ResolveListenUrls(
            null,
            blankConfig,
            null,
            defaultPort: 5000);

        resolvedFromAspNet.Should().Be("http://env.local:1002");
    }

    [Fact]
    public void ListenUrlResolver_ResolveListenUrls_FallsBackToDefaultWhenNothingConfigured()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var resolved = ListenUrlResolver.ResolveListenUrls(
            null,
            config,
            "Service:ListenUrls",
            defaultPort: 5010);

        resolved.Should().Be("http://localhost:5010");
    }

    [Fact]
    public void ResolveBrowserUrl_ReturnsTrimmedHttpAuthorityAndFallsBackDefault()
    {
        var browserUrlFromCandidate = ListenUrlResolver.ResolveBrowserUrl(
            "ftp://unsupported:9090; http://localhost:1000/path?query=1; https://127.0.0.1:2000",
            defaultPort: 5011);
        browserUrlFromCandidate.Should().Be("http://localhost:1000");

        var browserUrlFromWildcardHost = ListenUrlResolver.ResolveBrowserUrl(
            "http://*:7777/health; https://0.0.0.0:8888/",
            defaultPort: 5011);
        browserUrlFromWildcardHost.Should().Be("https://localhost:8888");

        var browserUrlDefault = ListenUrlResolver.ResolveBrowserUrl(
            "ftp://unsupported:9090;;   ",
            defaultPort: 5011);
        browserUrlDefault.Should().Be("http://localhost:5011");
    }

    [Fact]
    public void NyxIdLlmEndpointResolver_ResolveSpec_ShouldSupportDefaultGatewayAndRelativePath()
    {
        var gatewayConfig = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();

        NyxIdLlmEndpointResolver.ResolveSpec(gatewayConfig)
            .Should()
            .Be(new NyxIdLlmEndpointSpec(NyxIdLlmEndpointKind.Gateway));

        var relativeConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cli:App:NyxId:LlmEndpoint:Kind"] = "RelativePath",
                ["Cli:App:NyxId:LlmEndpoint:RelativePath"] = " llm/custom ",
            })
            .Build();

        NyxIdLlmEndpointResolver.ResolveSpec(relativeConfig)
            .Should()
            .Be(new NyxIdLlmEndpointSpec(NyxIdLlmEndpointKind.RelativePath, " llm/custom "));
    }

    [Fact]
    public void NyxIdLlmEndpointResolver_ResolveSpec_ShouldRejectUnsupportedKind()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:LlmEndpoint:Kind"] = "unsupported",
            })
            .Build();

        FluentActions.Invoking(() => NyxIdLlmEndpointResolver.ResolveSpec(config))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Unsupported NyxID LLM endpoint kind*");
    }

    [Fact]
    public void NyxIdLlmEndpointResolver_ResolveEndpoint_ShouldNormalizeAuthorityAndGatewayPath()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cli:App:NyxId:Authority"] = " https://nyxid.local/api/v1/llm/gateway/v1/ ",
            })
            .Build();

        var resolved = NyxIdLlmEndpointResolver.ResolveEndpoint(config);

        resolved.Should().Be("https://nyxid.local/api/v1/llm/gateway/v1");
    }

    [Fact]
    public void NyxIdLlmEndpointResolver_ResolveEndpoint_ShouldSupportRelativePathAndFallbackAuthorities()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:Authentication:Authority"] = "https://auth.local/",
                ["Aevatar:NyxId:LlmEndpoint:Kind"] = "RelativePath",
                ["Aevatar:NyxId:LlmEndpoint:RelativePath"] = "gateway/alt",
            })
            .Build();

        var resolved = NyxIdLlmEndpointResolver.ResolveEndpoint(config);

        resolved.Should().Be("https://auth.local/gateway/alt");
    }

    [Fact]
    public void NyxIdLlmEndpointResolver_ResolveEndpoint_ShouldReturnNullForInvalidAuthority()
    {
        NyxIdLlmEndpointResolver.ResolveEndpoint("not-a-uri", null).Should().BeNull();
        NyxIdLlmEndpointResolver.ResolveEndpoint("   ", null).Should().BeNull();
    }

    [Fact]
    public void NyxIdLlmEndpointResolver_ResolveEndpoint_ShouldRejectInvalidRelativePath()
    {
        var relativeSpec = new NyxIdLlmEndpointSpec(NyxIdLlmEndpointKind.RelativePath, "https://evil.local");

        FluentActions.Invoking(() => NyxIdLlmEndpointResolver.ResolveEndpoint("https://nyxid.local", relativeSpec))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*must not be an absolute URL*");
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
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
