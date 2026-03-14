using System.Collections;
using System.Reflection;
using System.Text.Json;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Bootstrap.Extensions.AI.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Bootstrap.Tests;

public class AIFeatureBootstrapCoverageTests
{
    [Fact]
    public void ReadConfiguredProviders_WhenProviderTypeMissing_ShouldInferProviderSemanticFromName()
    {
        var options = new AevatarAIFeatureOptions
        {
            OpenAIModel = "openai-default",
            DeepSeekModel = "deepseek-default",
            DefaultProvider = "openai",
        };
        var secretsStore = new InMemorySecretsStore(new Dictionary<string, string>
        {
            ["LLMProviders:Providers:deepseek:ApiKey"] = "deepseek-key",
        });

        var configuredProviders = InvokeReadConfiguredProviders(secretsStore, options);

        configuredProviders.Should().ContainSingle();
        var provider = configuredProviders[0];
        ReadConfiguredProviderString(provider, "Name").Should().Be("deepseek");
        ReadConfiguredProviderString(provider, "ProviderType").Should().Be("deepseek");
        ReadConfiguredProviderString(provider, "Model").Should().Be("deepseek-default");
        ReadConfiguredProviderString(provider, "Endpoint").Should().Be("https://api.deepseek.com/v1");
    }

    [Fact]
    public void AddAevatarAIFeatures_ShouldRegisterCoreServicesWithExplicitApiKey()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options =>
        {
            options.ApiKey = "demo-key";
            options.DefaultProvider = "deepseek";
            options.SecretsStore = new InMemorySecretsStore();
            options.EnableMEAIProviders = true;
            options.SecretsStore = new InMemorySecretsStore();
            options.EnableSkills = true;
            options.SkillDirectories.Add("./skills-a");
            options.EnableMCPTools = false;
            options.SecretsStore = new InMemorySecretsStore();
        });

        using var provider = services.BuildServiceProvider();
        provider.GetService<IRoleAgentTypeResolver>().Should().NotBeNull();

        var llmFactory = provider.GetRequiredService<ILLMProviderFactory>();
        llmFactory.GetDefault().Name.Should().Be("deepseek");

        var skillOptions = provider.GetRequiredService<SkillsOptions>();
        skillOptions.Directories.Should().ContainSingle().Which.Should().Be("./skills-a");
        provider.GetServices<IAgentToolSource>().Should().ContainSingle(x => x is SkillsAgentToolSource);
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenFailoverEnabled_ShouldRegisterFailoverFactory()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options =>
        {
            options.ApiKey = "demo-key";
            options.DefaultProvider = "openai";
            options.EnableMEAIProviders = true;
            options.EnableMEAIToTornadoFailover = true;
        });

        using var provider = services.BuildServiceProvider();
        var llmFactory = provider.GetRequiredService<ILLMProviderFactory>();

        llmFactory.Should().BeOfType<FailoverLLMProviderFactory>();
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenFailoverDisabled_ShouldRegisterMEAIFactory()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options =>
        {
            options.ApiKey = "demo-key";
            options.DefaultProvider = "openai";
            options.EnableMEAIProviders = true;
            options.EnableMEAIToTornadoFailover = false;
        });

        using var provider = services.BuildServiceProvider();
        var llmFactory = provider.GetRequiredService<ILLMProviderFactory>();

        llmFactory.Should().BeOfType<MEAILLMProviderFactory>();
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenOnlyDeepSeekEnvironmentKeyExists_ShouldBindDeepSeekFallbackProvider()
    {
        using var envScope = new EnvironmentVariablesScope(new Dictionary<string, string?>
        {
            ["DEEPSEEK_API_KEY"] = "deepseek-env-key",
            ["OPENAI_API_KEY"] = null,
            ["AEVATAR_LLM_API_KEY"] = null,
        });

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options =>
        {
            options.EnableMEAIProviders = true;
            options.DefaultProvider = "openai";
            options.SecretsStore = new InMemorySecretsStore();
        });

        using var provider = services.BuildServiceProvider();
        var llmFactory = provider.GetRequiredService<ILLMProviderFactory>();
        llmFactory.GetDefault().Name.Should().Be("deepseek");
        llmFactory.GetAvailableProviders().Should().ContainSingle().Which.Should().Be("deepseek");
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenDeepSeekAndOpenAIEnvironmentKeysBothExist_ShouldPreferDeepSeekFallbackProvider()
    {
        using var envScope = new EnvironmentVariablesScope(new Dictionary<string, string?>
        {
            ["DEEPSEEK_API_KEY"] = "deepseek-env-key",
            ["OPENAI_API_KEY"] = "openai-env-key",
            ["AEVATAR_LLM_API_KEY"] = null,
        });

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options =>
        {
            options.EnableMEAIProviders = true;
            options.DefaultProvider = "openai";
            options.SecretsStore = new InMemorySecretsStore();
        });

        using var provider = services.BuildServiceProvider();
        var llmFactory = provider.GetRequiredService<ILLMProviderFactory>();
        llmFactory.GetDefault().Name.Should().Be("deepseek");
        llmFactory.GetAvailableProviders().Should().ContainSingle().Which.Should().Be("deepseek");
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenMEAIDisabled_ShouldNotRegisterFactory()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options =>
        {
            options.EnableMEAIProviders = false;
            options.EnableMCPTools = false;
            options.EnableSkills = false;
        });

        using var provider = services.BuildServiceProvider();
        provider.GetService<ILLMProviderFactory>().Should().BeNull();
        provider.GetService<IRoleAgentTypeResolver>().Should().NotBeNull();
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenReloadableFactoryEnabled_ShouldPickUpdatedSecrets()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"ai-feature-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        var previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
        Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, tempHome);

        try
        {
            WriteFlatSecrets(
                AevatarPaths.SecretsJson,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["LLMProviders:Providers:openai:ApiKey"] = "openai-key",
                    ["LLMProviders:Providers:openai:ProviderType"] = "openai",
                    ["LLMProviders:Providers:openai:Model"] = "gpt-4o-mini",
                    ["LLMProviders:Default"] = "openai",
                });

            var services = new ServiceCollection();
            var config = new ConfigurationBuilder().Build();
            services.AddAevatarAIFeatures(config, options =>
            {
                options.EnableMEAIProviders = true;
                options.EnableMEAIToTornadoFailover = false;
                options.EnableReloadableProviderFactory = true;
            });

            using var provider = services.BuildServiceProvider();
            var llmFactory = provider.GetRequiredService<ILLMProviderFactory>();
            llmFactory.GetDefault().Name.Should().Be("openai");

            WriteFlatSecrets(
                AevatarPaths.SecretsJson,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["LLMProviders:Providers:deepseek:ApiKey"] = "deepseek-key",
                    ["LLMProviders:Providers:deepseek:ProviderType"] = "deepseek",
                    ["LLMProviders:Providers:deepseek:Model"] = "deepseek-chat",
                    ["LLMProviders:Default"] = "deepseek",
                });

            llmFactory.GetDefault().Name.Should().Be("deepseek");
            llmFactory.GetAvailableProviders().Should().ContainSingle().Which.Should().Be("deepseek");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, previousHome);
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task AddAevatarAIFeatures_WhenMCPEnabledAndConfigured_ShouldRegisterMCPToolSourceAndConnectorBuilder()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"ai-feature-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        var previousHome = Environment.GetEnvironmentVariable(AevatarPaths.HomeEnv);
        Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, tempHome);

        try
        {
            File.WriteAllText(Path.Combine(tempHome, "mcp.json"),
                """
                {
                  "mcpServers": {
                    "local": {
                      "command": "echo",
                      "args": ["hello"],
                      "env": { "K": "V" }
                    }
                  }
                }
                """);

            var services = new ServiceCollection();
            var config = new ConfigurationBuilder().Build();

            services.AddAevatarAIFeatures(config, options =>
            {
                options.EnableMEAIProviders = false;
                options.EnableMCPTools = true;
                options.EnableSkills = false;
            });

            await using var provider = services.BuildServiceProvider();

            provider.GetServices<IAgentToolSource>().Should().ContainSingle(x => x is MCPAgentToolSource);
            provider.GetServices<IConnectorBuilder>().Should().ContainSingle(x => x is MCPConnectorBuilder);

            var mcpOptions = provider.GetRequiredService<MCPToolsOptions>();
            mcpOptions.Servers.Should().ContainSingle();
            mcpOptions.Servers[0].Name.Should().Be("local");
            mcpOptions.Servers[0].Environment["K"].Should().Be("V");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AevatarPaths.HomeEnv, previousHome);
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void MCPConnectorBuilder_ShouldValidateCommandAndBuildConnector()
    {
        var builder = new MCPConnectorBuilder();

        var invalidEntry = new ConnectorConfigEntry
        {
            Name = "mcp-a",
            Type = "mcp",
            MCP = new MCPConnectorConfig { Command = "" },
        };

        var okInvalid = builder.TryBuild(invalidEntry, NullLogger.Instance, out var invalidConnector);
        okInvalid.Should().BeFalse();
        invalidConnector.Should().BeNull();

        var validEntry = new ConnectorConfigEntry
        {
            Name = "mcp-b",
            Type = "mcp",
            MCP = new MCPConnectorConfig
            {
                ServerName = "server-a",
                Command = "echo",
                Arguments = ["hello"],
                Environment = new Dictionary<string, string> { ["A"] = "B" },
                DefaultTool = "tool-a",
                AllowedTools = ["tool-a"],
                AllowedInputKeys = ["q"],
            },
        };

        var okValid = builder.TryBuild(validEntry, NullLogger.Instance, out var connector);
        okValid.Should().BeTrue();
        connector.Should().NotBeNull();
        connector!.Type.Should().Be("mcp");
        connector.Name.Should().Be("mcp-b");
    }

    private static IReadOnlyList<object> InvokeReadConfiguredProviders(
        IAevatarSecretsStore secretsStore,
        AevatarAIFeatureOptions options)
    {
        var method = typeof(global::Aevatar.Bootstrap.Extensions.AI.ServiceCollectionExtensions).GetMethod(
            "ReadConfiguredProviders",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = method!.Invoke(null, [secretsStore, options]);
        result.Should().NotBeNull();
        return ((IEnumerable)result!).Cast<object>().ToList();
    }

    private static string? ReadConfiguredProviderString(object configuredProvider, string propertyName)
    {
        var property = configuredProvider.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        property.Should().NotBeNull();
        return property!.GetValue(configuredProvider) as string;
    }

    private sealed class EnvironmentVariablesScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new(StringComparer.Ordinal);

        public EnvironmentVariablesScope(IReadOnlyDictionary<string, string?> overrides)
        {
            foreach (var pair in overrides)
            {
                _previousValues[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _previousValues)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private sealed class InMemorySecretsStore : IAevatarSecretsStore
    {
        private readonly Dictionary<string, string> _values;

        public InMemorySecretsStore(Dictionary<string, string>? seed = null)
        {
            _values = seed is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(seed, StringComparer.OrdinalIgnoreCase);
        }

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public string? GetApiKey(string providerName)
        {
            if (_values.TryGetValue($"LLMProviders:Providers:{providerName}:ApiKey", out var providerScoped) &&
                !string.IsNullOrWhiteSpace(providerScoped))
                return providerScoped;

            if (_values.TryGetValue($"LLMProviders:{providerName}:ApiKey", out var legacyScoped) &&
                !string.IsNullOrWhiteSpace(legacyScoped))
                return legacyScoped;

            if (_values.TryGetValue($"{providerName}_API_KEY", out var envScoped) &&
                !string.IsNullOrWhiteSpace(envScoped))
                return envScoped;

            return null;
        }

        public string? GetDefaultProvider() => Get("LLMProviders:Default");

        public IReadOnlyDictionary<string, string> GetAll() => _values;

        public void Set(string key, string value) => _values[key] = value;

        public void Remove(string key) => _values.Remove(key);
    }

    private static void WriteFlatSecrets(string path, IReadOnlyDictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(values);
        File.WriteAllText(path, json);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
    }
}
