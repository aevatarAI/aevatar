using Aevatar.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Integration.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class AevatarConfigLoaderSecretsTests
{
    [Fact]
    public void AddAevatarConfig_WhenPlaintextSecretsExistWithoutOptIn_ShouldFailClosed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-config-plaintext-denied-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "secrets.json");
        File.WriteAllText(path, """{"Custom:Secret":"plaintext-secret"}""");

        try
        {
            using var homeScope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, dir);
            using var secretsScope = new EnvironmentVariableScope(AevatarPaths.SecretsPathEnv, path);
            using var plaintextScope = new EnvironmentVariableScope(
                LocalSecretProtectionOptions.AllowPlaintextSecretsEnv,
                null);

            var act = () => new ConfigurationBuilder()
                .AddAevatarConfig()
                .Build();

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*plaintext*AEVATAR_ALLOW_PLAINTEXT_SECRETS*");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddAevatarConfig_WhenPlaintextSecretsOptInEnabled_ShouldLoadPlaintextSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-config-plaintext-allowed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "secrets.json");
        File.WriteAllText(path, """{"Custom:Secret":"plaintext-secret"}""");

        try
        {
            using var homeScope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, dir);
            using var secretsScope = new EnvironmentVariableScope(AevatarPaths.SecretsPathEnv, path);
            using var plaintextScope = new EnvironmentVariableScope(
                LocalSecretProtectionOptions.AllowPlaintextSecretsEnv,
                "true");

            var configuration = new ConfigurationBuilder()
                .AddAevatarConfig()
                .Build();

            configuration["Custom:Secret"].Should().Be("plaintext-secret");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddAevatarConfig_WhenLocalFileStoreDisabled_ShouldSkipSecretsJsonAndUseEnvironment()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-config-local-disabled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "secrets.json");
        File.WriteAllText(path, """{"Custom:Secret":"should-not-load"}""");

        try
        {
            using var homeScope = new EnvironmentVariableScope(AevatarPaths.HomeEnv, dir);
            using var secretsScope = new EnvironmentVariableScope(AevatarPaths.SecretsPathEnv, path);
            using var plaintextScope = new EnvironmentVariableScope(
                LocalSecretProtectionOptions.AllowPlaintextSecretsEnv,
                null);
            using var envSecretScope = new EnvironmentVariableScope("AEVATAR_Custom__Secret", "from-env");

            var configuration = new ConfigurationBuilder()
                .AddAevatarConfig(allowLocalFileStore: false)
                .Build();

            configuration["Custom:Secret"].Should().Be("from-env");
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
