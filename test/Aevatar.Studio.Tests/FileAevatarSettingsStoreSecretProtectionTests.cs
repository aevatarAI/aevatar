using Aevatar.Configuration;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using System.Security.Cryptography;

namespace Aevatar.Studio.Tests;

public sealed class FileAevatarSettingsStoreSecretProtectionTests
{
    private const string RawSecret = "RAW_SECRET_SHOULD_NOT_APPEAR";

    [Fact]
    public async Task SaveAsync_WhenEncryptionUnavailableWithoutPlaintextOptIn_ShouldFailClosed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-studio-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "secrets.json");

        try
        {
            WriteSiblingMasterKey(path);
            var store = new FileAevatarSettingsStore(path, NoPlaintextNoLocalMasterKeySources);

            var act = async () => await store.SaveAsync(CreateSettings(path));

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*plaintext*AEVATAR_ALLOW_PLAINTEXT_SECRETS*");
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WithPlaintextDevOptIn_ShouldPersistWhenEncryptionUnavailable()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-studio-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "secrets.json");

        try
        {
            WriteSiblingMasterKey(path);
            var store = new FileAevatarSettingsStore(path, PlaintextAllowedNoLocalMasterKeySources);

            await store.SaveAsync(CreateSettings(path));

            var text = await File.ReadAllTextAsync(path);
            text.Should().Contain(RawSecret);
            text.Should().NotContain("\"ciphertextB64\"");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WithLocalMasterKeySourcesAndNoPlaintextOptIn_ShouldPersistEncryptedAndReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-studio-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "secrets.json");

        try
        {
            WriteSiblingMasterKey(path);
            var store = new FileAevatarSettingsStore(path, NoPlaintextWithLocalMasterKeySources);

            await store.SaveAsync(CreateSettings(path));

            await AssertEncryptedAndReloads(path, NoPlaintextWithLocalMasterKeySources);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WithPlaintextDevOptInAndLocalMasterKeySources_ShouldPreferEncryptedStorage()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-studio-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "secrets.json");

        try
        {
            WriteSiblingMasterKey(path);
            var store = new FileAevatarSettingsStore(path, PlaintextAllowedWithLocalMasterKeySources);

            await store.SaveAsync(CreateSettings(path));

            await AssertEncryptedAndReloads(path, PlaintextAllowedWithLocalMasterKeySources);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task AssertEncryptedAndReloads(string path, LocalSecretProtectionOptions options)
    {
        var text = await File.ReadAllTextAsync(path);
        text.Should().NotContain(RawSecret);
        text.Should().Contain("\"ciphertextB64\"");

        var reloaded = await new FileAevatarSettingsStore(path, options).GetAsync();
        reloaded.Providers.Should().ContainSingle(provider => provider.ApiKey == RawSecret);
    }

    private static StoredAevatarSettings CreateSettings(string path) =>
        new(
            path,
            "openai",
            [],
            [new StoredLlmProvider(
                "openai",
                "openai",
                "OpenAI",
                "tier1",
                "test",
                "gpt-test",
                "https://api.openai.com",
                RawSecret,
                ApiKeyConfigured: true)]);

    private static LocalSecretProtectionOptions NoPlaintextNoLocalMasterKeySources =>
        new(false, LocalSecretMasterKeySource.Disabled);

    private static LocalSecretProtectionOptions PlaintextAllowedNoLocalMasterKeySources =>
        new(true, LocalSecretMasterKeySource.Disabled);

    private static LocalSecretProtectionOptions NoPlaintextWithLocalMasterKeySources =>
        new(false);

    private static LocalSecretProtectionOptions PlaintextAllowedWithLocalMasterKeySources =>
        new(true);

    private static void WriteSiblingMasterKey(string secretsPath)
    {
        var directory = Path.GetDirectoryName(secretsPath);
        Directory.CreateDirectory(directory!);
        File.WriteAllBytes(Path.Combine(directory!, "masterkey.bin"), RandomNumberGenerator.GetBytes(32));
    }
}
