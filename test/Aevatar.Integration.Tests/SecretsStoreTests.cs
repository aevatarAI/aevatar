using Aevatar.Configuration;
using FluentAssertions;

namespace Aevatar.Integration.Tests;

public class SecretsStoreTests
{
    [Fact]
    public void Constructor_ShouldLoadPlaintextJson_AndSupportLookupConventions()
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
            var store = new AevatarSecretsStore(path);

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
    public void SetAndRemove_ShouldPersistToFile_AsPlaintextWhenNoMasterKey()
    {
        var path = NewTempSecretsPath();

        try
        {
            var store = new AevatarSecretsStore(path);
            store.Set("K1", "V1");
            store.Set("K2", "V2");
            store.Remove("K1");

            // Set/Remove 持久化后重新加载仍能正确读取（无论明文或加密均成立）
            var reloaded = new AevatarSecretsStore(path);
            reloaded.Get("K1").Should().BeNull();
            reloaded.Get("K2").Should().Be("V2");

            // 明文文件内容断言：仅在无 master key 时文件为明文 JSON
            // 若机器配置了 macOS Keychain 或 masterkey.bin，Save() 会走 AES-256-GCM
            // 加密路径，文件内容为密文，无法直接匹配 key 字面量。
            // 此时 Set/Remove 正确性已通过上方 reload 验证，跳过明文断言。
            var text = File.ReadAllText(path);
            if (text.Contains("ciphertextB64", StringComparison.OrdinalIgnoreCase))
                return;

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
            var store = new AevatarSecretsStore(path);
            store.GetAll().Should().BeEmpty();

            store.Set("After", "Write");

            var reloaded = new AevatarSecretsStore(path);
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
}
