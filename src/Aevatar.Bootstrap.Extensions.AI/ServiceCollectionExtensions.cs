using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Agents;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Extensions.AI.Connectors;
using Aevatar.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Bootstrap.Extensions.AI;

public sealed class AevatarAIFeatureOptions
{
    public bool EnableMEAIProviders { get; set; } = true;
    public bool EnableMCPTools { get; set; }
    public bool EnableSkills { get; set; }
    public IAevatarSecretsStore? SecretsStore { get; set; }
    public string? ApiKey { get; set; }
    public string DefaultProvider { get; set; } = "openai";
    public string OpenAIModel { get; set; } = "gpt-4o-mini";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public List<string> SkillDirectories { get; } = [];
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarAIFeatures(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AevatarAIFeatureOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AevatarAIFeatureOptions();
        configure?.Invoke(options);

        services.TryAddSingleton<IRoleAgentTypeResolver, RoleGAgentTypeResolver>();
        RegisterMeaiProviders(services, configuration, options);

        if (options.EnableMCPTools)
        {
            RegisterMCPTools(services);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, MCPConnectorBuilder>());
        }

        if (options.EnableSkills)
            RegisterSkills(services, options);

        return services;
    }

    private static void RegisterMeaiProviders(
        IServiceCollection services,
        IConfiguration configuration,
        AevatarAIFeatureOptions options)
    {
        if (!options.EnableMEAIProviders)
            return;

        var secrets = options.SecretsStore ?? new AevatarSecretsStore();
        var configuredProviders = ReadConfiguredProviders(secrets, options);
        if (configuredProviders.Count > 0)
        {
            services.AddMEAIProviders(factory =>
            {
                foreach (var provider in configuredProviders)
                {
                    factory.RegisterOpenAI(
                        provider.Name,
                        provider.Model,
                        provider.ApiKey,
                        string.IsNullOrWhiteSpace(provider.Endpoint) ? null : provider.Endpoint);
                }

                var preferredDefault = secrets.GetDefaultProvider()
                    ?? configuration["Models:DefaultProvider"]
                    ?? options.DefaultProvider;
                var defaultName = configuredProviders.Any(p => string.Equals(p.Name, preferredDefault, StringComparison.OrdinalIgnoreCase))
                    ? preferredDefault
                    : configuredProviders[0].Name;
                factory.SetDefault(defaultName);
            });
            return;
        }

        var apiKey = options.ApiKey
            ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            services.AddMEAIProviders(_ => { });
            return;
        }

        var fallbackProvider = options.DefaultProvider;
        var fallbackModel = fallbackProvider.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            ? options.DeepSeekModel
            : options.OpenAIModel;
        var fallbackEndpoint = fallbackProvider.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            ? "https://api.deepseek.com/v1"
            : null;

        services.AddMEAIProviders(factory => factory
            .RegisterOpenAI(fallbackProvider, fallbackModel, apiKey, fallbackEndpoint)
            .SetDefault(fallbackProvider));
    }

    private static List<ConfiguredProvider> ReadConfiguredProviders(
        IAevatarSecretsStore secrets,
        AevatarAIFeatureOptions options)
    {
        const string prefix = "LLMProviders:Providers:";
        var all = secrets.GetAll();
        var names = all.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(key => key[prefix.Length..])
            .Select(rest =>
            {
                var splitIndex = rest.IndexOf(':');
                return splitIndex <= 0 ? string.Empty : rest[..splitIndex];
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var configured = new List<ConfiguredProvider>();
        foreach (var name in names)
        {
            var apiKey = secrets.GetApiKey(name);
            if (string.IsNullOrWhiteSpace(apiKey))
                continue;

            all.TryGetValue($"LLMProviders:Providers:{name}:ProviderType", out var providerType);
            all.TryGetValue($"LLMProviders:Providers:{name}:Model", out var model);
            all.TryGetValue($"LLMProviders:Providers:{name}:Endpoint", out var endpoint);

            var normalizedType = string.IsNullOrWhiteSpace(providerType) ? "openai" : providerType.Trim();
            var resolvedModel = string.IsNullOrWhiteSpace(model)
                ? (normalizedType.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ? options.DeepSeekModel : options.OpenAIModel)
                : model.Trim();
            var resolvedEndpoint = string.IsNullOrWhiteSpace(endpoint)
                ? (normalizedType.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ? "https://api.deepseek.com/v1" : null)
                : endpoint.Trim();

            configured.Add(new ConfiguredProvider(name.Trim(), normalizedType, resolvedModel, resolvedEndpoint, apiKey.Trim()));
        }

        return configured;
    }

    private sealed record ConfiguredProvider(
        string Name,
        string ProviderType,
        string Model,
        string? Endpoint,
        string ApiKey);

    private static void RegisterMCPTools(IServiceCollection services)
    {
        var servers = AevatarMCPConfig.LoadServers();
        if (servers.Count == 0)
            return;

        services.AddMCPTools(options =>
        {
            foreach (var server in servers)
            {
                options.Servers.Add(new MCPServerConfig
                {
                    Name = server.Name,
                    Command = server.Command,
                    Arguments = server.Args,
                    Environment = server.Env,
                });
            }
        });
    }

    private static void RegisterSkills(IServiceCollection services, AevatarAIFeatureOptions options)
    {
        services.AddSkills(skillOptions =>
        {
            if (options.SkillDirectories.Count == 0)
            {
                skillOptions.ScanDirectory("~/.aevatar/skills");
                skillOptions.ScanDirectory("./skills");
                return;
            }

            foreach (var directory in options.SkillDirectories)
                skillOptions.ScanDirectory(directory);
        });
    }
}
