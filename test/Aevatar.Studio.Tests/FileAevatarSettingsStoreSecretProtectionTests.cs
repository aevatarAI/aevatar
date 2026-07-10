using Aevatar.Configuration;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class FileAevatarSettingsStoreSecretProtectionTests
{
    [Fact]
    public async Task SaveAsync_WithFileMasterKeyAndNoPlaintextOptIn_ShouldPersistEncryptedAndReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-studio-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "secrets.json");

        try
        {
            WriteMasterKey(dir);
            var store = new FileAevatarSettingsStore(path, LocalSecretProtectionOptions.NoPlaintextNoKeychain);
            await store.SaveAsync(CreateSettings(path));

            var text = await File.ReadAllTextAsync(path);
            text.Should().NotContain("RAW_SECRET_SHOULD_NOT_APPEAR");
            text.Should().Contain("\"ciphertextB64\"");

            var reloaded = await new FileAevatarSettingsStore(path, LocalSecretProtectionOptions.NoPlaintextNoKeychain)
                .GetAsync();
            reloaded.Providers.Should().ContainSingle(provider => provider.ApiKey == "RAW_SECRET_SHOULD_NOT_APPEAR");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WithPlaintextDevOptInAndFileMasterKey_ShouldPreferEncryptedStorage()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-studio-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "secrets.json");

        try
        {
            WriteMasterKey(dir);
            var store = new FileAevatarSettingsStore(path, LocalSecretProtectionOptions.DevelopmentPlaintextNoKeychain);
            await store.SaveAsync(CreateSettings(path));

            var text = await File.ReadAllTextAsync(path);
            text.Should().NotContain("RAW_SECRET_SHOULD_NOT_APPEAR");
            text.Should().Contain("\"ciphertextB64\"");

            var reloaded = await new FileAevatarSettingsStore(path, LocalSecretProtectionOptions.DevelopmentPlaintextNoKeychain)
                .GetAsync();
            reloaded.Providers.Should().ContainSingle(provider => provider.ApiKey == "RAW_SECRET_SHOULD_NOT_APPEAR");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
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
                "RAW_SECRET_SHOULD_NOT_APPEAR",
                ApiKeyConfigured: true)]);

    private static void WriteMasterKey(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "masterkey.bin"), Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray());
    }
}
