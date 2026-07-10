using Aevatar.Configuration;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class FileAevatarSettingsStoreSecretProtectionTests
{
    [Fact]
    public async Task SaveAsync_WithoutPlaintextOptIn_ShouldFailClosedOrPersistEncrypted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-studio-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "secrets.json");

        try
        {
            var store = new FileAevatarSettingsStore(path, LocalSecretProtectionOptions.NoPlaintextNoKeychain);
            var settings = new StoredAevatarSettings(
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

            try
            {
                await store.SaveAsync(settings);
            }
            catch (InvalidOperationException ex)
            {
                ex.Message.Should().Contain("plaintext").And.Contain("AEVATAR_ALLOW_PLAINTEXT_SECRETS");
                File.Exists(path).Should().BeFalse();
                return;
            }

            var text = await File.ReadAllTextAsync(path);
            text.Should().Contain("ciphertextB64");
            text.Should().NotContain("RAW_SECRET_SHOULD_NOT_APPEAR");

            var reloaded = await new FileAevatarSettingsStore(path, LocalSecretProtectionOptions.NoPlaintextNoKeychain)
                .GetAsync();
            reloaded.Providers.Should().ContainSingle(provider =>
                provider.ProviderName == "openai" &&
                provider.ApiKey == "RAW_SECRET_SHOULD_NOT_APPEAR");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WithPlaintextDevOptIn_ShouldPersistWithAvailableProtection()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aevatar-studio-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "secrets.json");

        try
        {
            var store = new FileAevatarSettingsStore(path, LocalSecretProtectionOptions.DevelopmentPlaintextNoKeychain);
            var settings = new StoredAevatarSettings(
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

            await store.SaveAsync(settings);

            var text = await File.ReadAllTextAsync(path);
            if (text.Contains("ciphertextB64", StringComparison.Ordinal))
            {
                text.Should().NotContain("RAW_SECRET_SHOULD_NOT_APPEAR");

                var reloaded = await new FileAevatarSettingsStore(path, LocalSecretProtectionOptions.DevelopmentPlaintextNoKeychain)
                    .GetAsync();
                reloaded.Providers.Should().ContainSingle(provider =>
                    provider.ProviderName == "openai" &&
                    provider.ApiKey == "RAW_SECRET_SHOULD_NOT_APPEAR");
            }
            else
            {
                text.Should().Contain("RAW_SECRET_SHOULD_NOT_APPEAR");
            }
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
