using System.Text.Json;
using Aevatar.Configuration;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

[Collection("EnvironmentVariables")]
public sealed class FileAevatarSettingsStoreTests
{
    [Fact]
    public async Task GetAsync_ShouldIncludeNyxIdProviderTypeWithGatewayEndpointFromAuthority()
    {
        using var scope = new AevatarHomeScope();
        scope.WriteConfig(
            """
            {
              "Cli": {
                "App": {
                  "NyxId": {
                    "Authority": "https://nyx.example.com"
                  }
                }
              }
            }
            """);

        var store = new FileAevatarSettingsStore();

        var settings = await store.GetAsync();

        var nyxId = settings.ProviderTypes.Single(provider => provider.Id == "nyxid");
        nyxId.DisplayName.Should().Be("NyxID Gateway");
        nyxId.DefaultEndpoint.Should().Be("https://nyx.example.com/api/v1/llm/gateway/v1");
        nyxId.DefaultModel.Should().Be("gpt-5.4");
    }

    [Fact]
    public async Task GetAsync_ShouldResolveNyxIdProviderEndpointFromAuthorityWhenSecretEndpointMissing()
    {
        using var scope = new AevatarHomeScope();
        scope.WriteConfig(
            """
            {
              "Cli": {
                "App": {
                  "NyxId": {
                    "Authority": "https://nyx.example.com"
                  }
                }
              }
            }
            """);
        scope.WriteSecrets(new Dictionary<string, string>
        {
            ["LLMProviders:Providers:nyxid:ProviderType"] = "nyxid",
            ["LLMProviders:Providers:nyxid:Model"] = "claude-sonnet-4-5-20250929",
            ["LLMProviders:Providers:nyxid:ApiKey"] = "nyx-token",
        });

        var store = new FileAevatarSettingsStore();

        var settings = await store.GetAsync();

        var nyxId = settings.Providers.Single(provider => provider.ProviderName == "nyxid");
        nyxId.ProviderType.Should().Be("nyxid");
        nyxId.Endpoint.Should().Be("https://nyx.example.com/api/v1/llm/gateway/v1");
        nyxId.Model.Should().Be("claude-sonnet-4-5-20250929");
        nyxId.ApiKeyConfigured.Should().BeTrue();
    }

    private sealed class AevatarHomeScope : IDisposable
    {
        private readonly string? _previousHome;
        private readonly string? _previousSecretsPath;

        public AevatarHomeScope()
        {
            Root = Path.Combine(Path.GetTempPath(), $"aevatar-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);

            _previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
            _previousSecretsPath = Environment.GetEnvironmentVariable(AevatarPaths.SecretsPathEnv);

            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, Root);
            Environment.SetEnvironmentVariable(AevatarPaths.SecretsPathEnv, null);
        }

        public string Root { get; }

        public void WriteConfig(string json)
        {
            File.WriteAllText(AevatarPaths.ConfigJson, json);
        }

        public void WriteSecrets(IReadOnlyDictionary<string, string> values)
        {
            var payload = JsonSerializer.Serialize(values);
            File.WriteAllText(AevatarPaths.SecretsJson, payload);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, _previousHome);
            Environment.SetEnvironmentVariable(AevatarPaths.SecretsPathEnv, _previousSecretsPath);

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollectionDefinition;
