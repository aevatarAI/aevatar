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
    public string DefaultProvider { get; set; } = "deepseek";
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

        var apiKey = options.ApiKey
            ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");

        var provider = options.DefaultProvider;

        if (string.IsNullOrEmpty(apiKey))
        {
            var secrets = options.SecretsStore ?? new AevatarSecretsStore();
            provider = secrets.GetDefaultProvider() ?? configuration["Models:DefaultProvider"] ?? options.DefaultProvider;
            apiKey = secrets.GetApiKey(provider);
            if (string.IsNullOrEmpty(apiKey))
            {
                foreach (var fallback in new[] { "deepseek", "openai" })
                {
                    apiKey = secrets.GetApiKey(fallback);
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        provider = fallback;
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            services.AddMEAIProviders(_ => { });
            return;
        }

        var useDeepSeek = provider.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
        if (useDeepSeek)
        {
            services.AddMEAIProviders(factory => factory
                .RegisterOpenAI("deepseek", options.DeepSeekModel, apiKey, baseUrl: "https://api.deepseek.com/v1")
                .SetDefault("deepseek"));
            return;
        }

        services.AddMEAIProviders(factory => factory
            .RegisterOpenAI("openai", options.OpenAIModel, apiKey)
            .SetDefault("openai"));
    }

    private static void RegisterMCPTools(IServiceCollection services)
    {
        // Merged server map — connectors.json entries win on name collision.
        var serverMap = new Dictionary<string, MCPServerConfig>(StringComparer.OrdinalIgnoreCase);

        // 1. Legacy mcp.json (lowest priority).
        foreach (var entry in AevatarMCPConfig.LoadServers())
        {
            serverMap.TryAdd(entry.Name, new MCPServerConfig
            {
                Name = entry.Name,
                Command = entry.Command,
                Arguments = entry.Args,
                Environment = entry.Env,
            });
        }

        // 2. connectors.json MCP entries (highest priority).
        foreach (var connector in AevatarConnectorConfig.LoadConnectors())
        {
            if (!string.Equals(connector.Type, "mcp", StringComparison.OrdinalIgnoreCase))
                continue;

            var server = MCPConnectorBuilder.ToMCPServerConfig(connector);
            if (server is not null)
                serverMap[server.Name] = server;
        }

        // Always register the MCP tool source so MCPAgentToolSource is available.
        services.AddMCPTools(options =>
        {
            foreach (var server in serverMap.Values)
                options.Servers.Add(server);
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
