using System.Text;
using System.Text.Json;
using Aevatar.Tools.Cli.Hosting;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public class OpenClawProviderSyncTests
{
    [Fact]
    public void BuildPlan_WhenProviderConflicts_ShouldPreferAevatarAndImportOpenClawOnly()
    {
        var aevatar = new OpenClawProviderSet(
            DefaultProvider: "openai",
            Providers: new Dictionary<string, OpenClawProviderSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new("openai", "gpt-4o", "https://api.openai.com/v1", "sk-aevatar-openai"),
                ["deepseek"] = new("deepseek", "deepseek-chat", "https://api.deepseek.com", "sk-aevatar-deepseek"),
            });
        var openClaw = new OpenClawProviderSet(
            DefaultProvider: "anthropic",
            Providers: new Dictionary<string, OpenClawProviderSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new("openai", "gpt-4.1", "https://gateway.openclaw.local/v1", "sk-openclaw-openai"),
                ["anthropic"] = new("anthropic", "claude-3-5-sonnet", "https://api.anthropic.com/v1", "sk-openclaw-anthropic"),
            });

        var plan = OpenClawProviderSyncPlanner.BuildPlan(
            aevatar,
            openClaw,
            mode: "bidirectional",
            precedence: "aevatar",
            aevatarSecretsPath: "/tmp/aevatar-secrets.json",
            openClawConfigPath: "/tmp/openclaw.json");

        plan.EffectiveDefaultProvider.Should().Be("openai");
        plan.AevatarTarget.Providers.Should().ContainKey("anthropic");
        plan.OpenClawTarget.Providers.Should().ContainKey("deepseek");
        plan.OpenClawTarget.Providers["openai"].Model.Should().Be("gpt-4o");
        plan.OpenClawTarget.Providers["openai"].Endpoint.Should().Be("https://api.openai.com/v1");
        plan.OpenClawTarget.Providers["openai"].ApiKey.Should().Be("sk-aevatar-openai");
        plan.AevatarChanges.Should().BeTrue();
        plan.OpenClawChanges.Should().BeTrue();
    }

    [Fact]
    public async Task PlanAsync_ShouldEmitDryRunJsonSummary()
    {
        var sandbox = CreateSandbox();
        try
        {
            var secretsPath = Path.Combine(sandbox, "secrets.json");
            var openClawPath = Path.Combine(sandbox, "openclaw.json");
            WriteSecrets(secretsPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LLMProviders:Default"] = "openai",
                ["LLMProviders:Providers:openai:ProviderType"] = "openai",
                ["LLMProviders:Providers:openai:Model"] = "gpt-4o",
                ["LLMProviders:Providers:openai:Endpoint"] = "https://api.openai.com/v1",
                ["LLMProviders:Providers:openai:ApiKey"] = "sk-aevatar-openai",
            });
            WriteOpenClawConfig(openClawPath, """
            {
              "llm": {
                "defaultProvider": "anthropic",
                "providers": {
                  "openai": {
                    "providerType": "openai",
                    "model": "gpt-4.1",
                    "endpoint": "https://gateway.openclaw.local/v1",
                    "apiKey": "sk-openclaw-openai"
                  },
                  "anthropic": {
                    "providerType": "anthropic",
                    "model": "claude-3-5-sonnet",
                    "endpoint": "https://api.anthropic.com/v1",
                    "apiKey": "sk-openclaw-anthropic"
                  }
                }
              }
            }
            """);

            var output = await CaptureStdOutAsync(async () =>
                await OpenClawSyncCommandHandler.PlanAsync(
                    mode: "bidirectional",
                    precedence: "aevatar",
                    dryRun: true,
                    openClawConfigPath: openClawPath,
                    aevatarSecretsPath: secretsPath,
                    CancellationToken.None));

            output.ExitCode.Should().Be(0);
            using var doc = JsonDocument.Parse(output.StdOut);
            doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("dryRun").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("effectiveDefaultProvider").GetString().Should().Be("openai");
            doc.RootElement.GetProperty("providers").GetArrayLength().Should().Be(2);
        }
        finally
        {
            SafeDeleteDirectory(sandbox);
        }
    }

    [Fact]
    public async Task ApplyAsync_ShouldWriteBothSides_AndReassertDefaultProvider()
    {
        var sandbox = CreateSandbox();
        try
        {
            var secretsPath = Path.Combine(sandbox, "secrets.json");
            var openClawPath = Path.Combine(sandbox, "openclaw.json");
            WriteSecrets(secretsPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LLMProviders:Default"] = "openai",
                ["LLMProviders:Providers:openai:ProviderType"] = "openai",
                ["LLMProviders:Providers:openai:Model"] = "gpt-4o",
                ["LLMProviders:Providers:openai:Endpoint"] = "https://api.openai.com/v1",
                ["LLMProviders:Providers:openai:ApiKey"] = "sk-aevatar-openai",
            });
            WriteOpenClawConfig(openClawPath, """
            {
              "llm": {
                "defaultProvider": "anthropic",
                "providers": {
                  "openai": {
                    "providerType": "openai",
                    "model": "gpt-4.1",
                    "endpoint": "https://gateway.openclaw.local/v1",
                    "apiKey": "sk-openclaw-openai"
                  },
                  "anthropic": {
                    "providerType": "anthropic",
                    "model": "claude-3-5-sonnet",
                    "endpoint": "https://api.anthropic.com/v1",
                    "apiKey": "sk-openclaw-anthropic"
                  }
                }
              }
            }
            """);

            var output = await CaptureStdOutAsync(async () =>
                await OpenClawSyncCommandHandler.ApplyAsync(
                    mode: "bidirectional",
                    precedence: "aevatar",
                    createBackup: false,
                    openClawConfigPath: openClawPath,
                    aevatarSecretsPath: secretsPath,
                    CancellationToken.None));

            output.ExitCode.Should().Be(0);
            using (var doc = JsonDocument.Parse(output.StdOut))
            {
                doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
                doc.RootElement.GetProperty("result").GetProperty("aevatarUpdated").GetBoolean().Should().BeTrue();
                doc.RootElement.GetProperty("result").GetProperty("openClawUpdated").GetBoolean().Should().BeTrue();
            }

            var updatedAevatar = OpenClawProviderSyncPersistence.ReadAevatarState(secretsPath);
            var updatedOpenClaw = OpenClawProviderSyncPersistence.LoadOpenClawDocument(openClawPath).State;

            updatedAevatar.DefaultProvider.Should().Be("openai");
            updatedOpenClaw.DefaultProvider.Should().Be("openai");
            updatedAevatar.Providers.Should().ContainKey("anthropic");
            updatedAevatar.Providers["anthropic"].ApiKey.Should().Be("sk-openclaw-anthropic");
            updatedOpenClaw.Providers["openai"].Model.Should().Be("gpt-4o");
            updatedOpenClaw.Providers["openai"].ApiKey.Should().Be("sk-aevatar-openai");
        }
        finally
        {
            SafeDeleteDirectory(sandbox);
        }
    }

    private static string CreateSandbox()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "aevatar-openclaw-sync-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteSecrets(string path, IReadOnlyDictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(values, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(path, json + Environment.NewLine, Encoding.UTF8);
    }

    private static void WriteOpenClawConfig(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json.Trim() + Environment.NewLine, Encoding.UTF8);
    }

    private static async Task<(int ExitCode, string StdOut)> CaptureStdOutAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var exitCode = await action();
            await writer.FlushAsync();
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
