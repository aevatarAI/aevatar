using System.Security.Cryptography;
using Aevatar.Configuration;
using FluentAssertions;

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
