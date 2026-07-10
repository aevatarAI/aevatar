using Aevatar.Configuration;
using FluentAssertions;
using System.Security.Cryptography;

namespace Aevatar.Integration.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public class SecretsStoreTests
{
    [Fact]
    public void Constructor_WithPlaintextDevOptIn_ShouldLoadPlaintextJson_AndSupportLookupConventions()
    {
        var path = NewTempSecretsPath();
        File.WriteAllText(path, """
            {
              "LLMProviders:Providers:deepseek:ApiKey": "k1",
              "LLMProviders:openai:ApiKey": "k2",
              "DEEPSEEK_API_KEY": "k3",
              "LLMProviders:Default": "deepseek",
              "Custom:Value": "x"
            }
            """);

        try
        {
            var store = new AevatarSecretsStore(path, PlaintextAllowedNoLocalMasterKeySources);

            store.Get("Custom:Value").Should().Be("x");
            store.GetApiKey("deepseek").Should().Be("k1");
            store.GetApiKey("openai").Should().Be("k2");
            store.GetApiKey("DEEPSEEK").Should().Be("k3");
            store.GetDefaultProvider().Should().Be("deepseek");
            store.GetAll().Should().ContainKey("Custom:Value");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_WhenPlaintextExistsWithoutOptIn_ShouldFailClosed()
    {
        var path = NewTempSecretsPath();
        File.WriteAllText(path, """{"Custom:Value":"x"}""");

        try
        {
            var act = () => new AevatarSecretsStore(path, NoPlaintextNoLocalMasterKeySources);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*plaintext*AEVATAR_ALLOW_PLAINTEXT_SECRETS*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AevatarSecretsStore_WithInvalidEncryptedEnvelope_ShouldFailClosedWithoutPlaintextOptIn()
    {
        var path = NewTempSecretsPath();
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
            using var plaintextScope = new EnvironmentVariableScope(
                LocalSecretProtectionOptions.AllowPlaintextSecretsEnv,
                null);

            var act = () => new AevatarSecretsStore(path);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*plaintext*AEVATAR_ALLOW_PLAINTEXT_SECRETS*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SetAndRemove_WhenEncryptionUnavailableWithoutPlaintextOptIn_ShouldFailClosed()
    {
        var path = NewTempSecretsPath();

        try
        {
            WriteSiblingMasterKey(path);
            var store = new AevatarSecretsStore(path, NoPlaintextNoLocalMasterKeySources);

            var act = () => store.Set("K1", "V1");

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*plaintext*AEVATAR_ALLOW_PLAINTEXT_SECRETS*");
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
            TryDelete(path + ".tmp");
        }
    }

    [Fact]
    public void SetAndRemove_WithPlaintextDevOptIn_ShouldPersistToFile_AsPlaintextWhenNoMasterKey()
    {
        var path = NewTempSecretsPath();

        try
        {
            WriteSiblingMasterKey(path);
            var store = new AevatarSecretsStore(path, PlaintextAllowedNoLocalMasterKeySources);
            store.Set("K1", "V1");
            store.Set("K2", "V2");
            store.Remove("K1");

            var reloaded = new AevatarSecretsStore(path, PlaintextAllowedNoLocalMasterKeySources);
            reloaded.Get("K1").Should().BeNull();
            reloaded.Get("K2").Should().Be("V2");

            var text = File.ReadAllText(path);
            text.Should().Contain("\"K2\"");
            text.Should().NotContain("\"K1\"");
        }
        finally
        {
            File.Delete(path);
            TryDelete(path + ".tmp");
        }
    }

    [Fact]
    public void Constructor_WhenInvalidJson_ShouldFallbackToEmptyAndStillWritable()
    {
        var path = NewTempSecretsPath();
        File.WriteAllText(path, "{ invalid json");

        try
        {
            var store = new AevatarSecretsStore(path, PlaintextAllowedNoLocalMasterKeySources);
            store.GetAll().Should().BeEmpty();

            store.Set("After", "Write");

            var reloaded = new AevatarSecretsStore(path, PlaintextAllowedNoLocalMasterKeySources);
            reloaded.Get("After").Should().Be("Write");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string NewTempSecretsPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aevatar-secrets-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "secrets.json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // no-op
        }
    }

    private static LocalSecretProtectionOptions NoPlaintextNoLocalMasterKeySources =>
        new(false, LocalSecretMasterKeySource.Disabled);

    private static LocalSecretProtectionOptions PlaintextAllowedNoLocalMasterKeySources =>
        new(true, LocalSecretMasterKeySource.Disabled);

    private static void WriteSiblingMasterKey(string secretsPath)
    {
        var directory = Path.GetDirectoryName(secretsPath);
        Directory.CreateDirectory(directory!);
        File.WriteAllBytes(Path.Combine(directory!, "masterkey.bin"), RandomNumberGenerator.GetBytes(32));
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
