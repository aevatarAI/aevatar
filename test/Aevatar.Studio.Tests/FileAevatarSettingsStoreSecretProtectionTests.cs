using Aevatar.Configuration;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class FileAevatarSettingsStoreSecretProtectionTests
{
    [Fact]
    public async Task SaveAsync_WhenEncryptionUnavailableWithoutPlaintextOptIn_ShouldFailClosed()
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

            var act = async () => await store.SaveAsync(settings);

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
            text.Should().Contain("RAW_SECRET_SHOULD_NOT_APPEAR");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
