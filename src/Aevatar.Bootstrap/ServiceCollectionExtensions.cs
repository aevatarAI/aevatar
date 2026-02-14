using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Configuration;
using Aevatar.Foundation.Runtime.DependencyInjection;
using Aevatar.Workflows.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Bootstrap;

public sealed class AevatarBootstrapOptions
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
    public static IServiceCollection AddAevatarBootstrap(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AevatarBootstrapOptions>? configure = null)
    {
        var options = new AevatarBootstrapOptions();
        configure?.Invoke(options);

        services.AddAevatarConfig();
        services.AddAevatarRuntime();
        services.AddAevatarCognitive();

        RegisterMeaiProviders(services, configuration, options);

        if (options.EnableMCPTools)
            RegisterMcpTools(services);

        if (options.EnableSkills)
            RegisterSkills(services, options);

        return services;
    }

    private static void RegisterMeaiProviders(
        IServiceCollection services,
        IConfiguration configuration,
        AevatarBootstrapOptions options)
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

    private static void RegisterMcpTools(IServiceCollection services)
    {
        var servers = AevatarMcpConfig.LoadServers();
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

    private static void RegisterSkills(IServiceCollection services, AevatarBootstrapOptions options)
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
