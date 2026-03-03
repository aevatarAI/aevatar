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
    public List<string> SkillDirectories { get; } = [];

    /// <summary>
    /// Factory producing HttpMessageHandler instances for custom auth.
    /// Called once per handler (LLM, each MCP server with Auth config).
    /// All instances share the same underlying token service via DI.
    /// </summary>
    public Func<IServiceProvider, HttpMessageHandler>? AuthHandlerFactory { get; set; }
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
            RegisterMCPTools(services, options);
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

        // Step 1: LLM_* env vars (highest priority).
        var llmProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER");
        if (!string.IsNullOrEmpty(llmProvider))
        {
            var llmModel = Environment.GetEnvironmentVariable("LLM_MODEL")
                ?? throw new InvalidOperationException(
                    "LLM_PROVIDER is set but LLM_MODEL is missing. " +
                    "LLM_MODEL is required when LLM_PROVIDER is set.");

            if (options.AuthHandlerFactory is { } handlerFactory)
            {
                // Custom auth handler mode (e.g. NyxID token).
                // API key is not needed; base URL is required.
                var llmBaseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL")
                    ?? throw new InvalidOperationException(
                        "LLM_PROVIDER is set with AuthHandlerFactory but LLM_BASE_URL is missing. " +
                        "LLM_BASE_URL is required when using custom auth.");

                services.AddMEAIProviders((sp, factory) => factory
                    .RegisterOpenAI(llmProvider, llmModel, llmBaseUrl, handlerFactory(sp))
                    .SetDefault(llmProvider));
                return;
            }

            var llmApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY")
                ?? throw new InvalidOperationException(
                    "LLM_PROVIDER is set but LLM_API_KEY is missing. " +
                    "Both LLM_MODEL and LLM_API_KEY are required when LLM_PROVIDER is set.");
            var llmBaseUrlStd = Environment.GetEnvironmentVariable("LLM_BASE_URL");

            services.AddMEAIProviders(factory => factory
                .RegisterOpenAI(llmProvider, llmModel, llmApiKey, baseUrl: llmBaseUrlStd)
                .SetDefault(llmProvider));
            return;
        }

        // Step 2: AevatarSecretsStore fallback.
        var secrets = options.SecretsStore ?? new AevatarSecretsStore();
        var provider = secrets.GetDefaultProvider() ?? configuration["Models:DefaultProvider"];
        if (!string.IsNullOrEmpty(provider))
        {
            var apiKey = secrets.GetApiKey(provider);
            if (!string.IsNullOrEmpty(apiKey))
            {
                var (model, baseUrl) = ResolveModelDefaults(provider);
                services.AddMEAIProviders(factory => factory
                    .RegisterOpenAI(provider, model, apiKey, baseUrl: baseUrl)
                    .SetDefault(provider));
                return;
            }
        }

        foreach (var fallback in new[] { "deepseek", "openai" })
        {
            var apiKey = secrets.GetApiKey(fallback);
            if (!string.IsNullOrEmpty(apiKey))
            {
                var (model, baseUrl) = ResolveModelDefaults(fallback);
                services.AddMEAIProviders(factory => factory
                    .RegisterOpenAI(fallback, model, apiKey, baseUrl: baseUrl)
                    .SetDefault(fallback));
                return;
            }
        }

        // Step 3: Legacy env vars.
        (string envVar, string inferredProvider)[] legacyEnvVars =
        [
            ("DEEPSEEK_API_KEY", "deepseek"),
            ("OPENAI_API_KEY", "openai"),
            ("AEVATAR_LLM_API_KEY", "openai"),
        ];

        foreach (var (envVar, inferredProvider) in legacyEnvVars)
        {
            var apiKey = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(apiKey))
            {
                var (model, baseUrl) = ResolveModelDefaults(inferredProvider);
                services.AddMEAIProviders(factory => factory
                    .RegisterOpenAI(inferredProvider, model, apiKey, baseUrl: baseUrl)
                    .SetDefault(inferredProvider));
                return;
            }
        }

        // Step 4: No provider configured.
        throw new InvalidOperationException(
            "No LLM provider configured. Set LLM_PROVIDER + LLM_MODEL + LLM_API_KEY env vars, " +
            "or configure via AevatarSecretsStore (~/.aevatar/secrets.json).");
    }

    private static (string model, string? baseUrl) ResolveModelDefaults(string provider) =>
        provider.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            ? ("deepseek-chat", "https://api.deepseek.com/v1")
            : ("gpt-4o-mini", null);

    private static void RegisterMCPTools(IServiceCollection services, AevatarAIFeatureOptions options)
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

        if (options.AuthHandlerFactory is { } handlerFactory)
        {
            // Deferred registration — resolve handler at service-provider time.
            services.AddMCPTools((sp, mcpOptions) =>
            {
                foreach (var server in serverMap.Values)
                {
                    if (server.Auth is not null)
                        server.AuthHandler = handlerFactory(sp);
                    mcpOptions.Servers.Add(server);
                }
            });
        }
        else
        {
            // Immediate registration (existing behavior).
            services.AddMCPTools(mcpOptions =>
            {
                foreach (var server in serverMap.Values)
                    mcpOptions.Servers.Add(server);
            });
        }
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
