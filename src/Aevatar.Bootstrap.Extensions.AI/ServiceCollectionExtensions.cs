using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Agents;
using Aevatar.AI.Core.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.LLMProviders.Tornado;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Extensions.AI.Connectors;
using Aevatar.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Extensions.AI;

public sealed class AevatarAIFeatureOptions
{
    public bool EnableMEAIProviders { get; set; } = true;
    public bool EnableMEAIToTornadoFailover { get; set; } = true;
    public bool EnableReloadableProviderFactory { get; set; }
    public bool FailoverFallbackToDefaultProviderWhenNamedProviderMissing { get; set; } = true;
    public bool FailoverPreferFallbackDefaultProvider { get; set; } = true;
    public bool FailoverPreferDeepSeekAsFallbackDefault { get; set; } = true;
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

        var secretsStoreAccessor = CreateSecretsStoreAccessor(options);
        if (options.EnableReloadableProviderFactory)
        {
            var versionProvider = BuildProviderConfigVersionProvider(options);
            services.TryAddSingleton<ILLMProviderFactory>(sp =>
            {
                var logger = sp.GetService<ILogger<ReloadableLLMProviderFactory>>();
                return new ReloadableLLMProviderFactory(
                    () => BuildLlmProviderFactory(configuration, options, secretsStoreAccessor),
                    versionProvider,
                    logger);
            });
            return;
        }

        var factory = BuildLlmProviderFactory(configuration, options, secretsStoreAccessor);
        services.TryAddSingleton<ILLMProviderFactory>(factory);
    }

    private static ILLMProviderFactory BuildLlmProviderFactory(
        IConfiguration configuration,
        AevatarAIFeatureOptions options,
        Func<IAevatarSecretsStore> secretsStoreAccessor)
    {
        var secrets = secretsStoreAccessor();
        var configuredProviders = ReadConfiguredProviders(secrets, options);
        if (configuredProviders.Count == 0)
        {
            var fallbackRegistration = ResolveFallbackRegistration(options);
            if (fallbackRegistration != null)
            {
                configuredProviders.Add(new ConfiguredProvider(
                    fallbackRegistration.ProviderName,
                    fallbackRegistration.ProviderName,
                    fallbackRegistration.Model,
                    fallbackRegistration.Endpoint,
                    fallbackRegistration.ApiKey));
            }
        }

        var preferredDefault = secrets.GetDefaultProvider()
            ?? configuration["Models:DefaultProvider"]
            ?? options.DefaultProvider;
        var defaultName = ResolveDefaultProviderName(configuredProviders, preferredDefault);

        var meaiFactory = BuildMeaiFactory(configuredProviders, defaultName);
        if (!options.EnableMEAIToTornadoFailover)
            return meaiFactory;

        var tornadoDefaultName = ResolveTornadoDefaultProviderName(configuredProviders, defaultName, options);
        var tornadoFactory = BuildTornadoFactory(configuredProviders, tornadoDefaultName);
        return new FailoverLLMProviderFactory(
            meaiFactory,
            tornadoFactory,
            new LLMProviderFailoverOptions
            {
                FallbackToDefaultProviderWhenNamedProviderMissing =
                    options.FailoverFallbackToDefaultProviderWhenNamedProviderMissing,
                PreferFallbackDefaultProvider = options.FailoverPreferFallbackDefaultProvider,
            });
    }

    private static string ResolveTornadoDefaultProviderName(
        IReadOnlyList<ConfiguredProvider> configuredProviders,
        string defaultName,
        AevatarAIFeatureOptions options)
    {
        if (!options.FailoverPreferDeepSeekAsFallbackDefault || configuredProviders.Count == 0)
            return defaultName;

        var deepSeek = configuredProviders.FirstOrDefault(static p =>
            string.Equals(p.ProviderType, "deepseek", StringComparison.OrdinalIgnoreCase));
        return deepSeek?.Name ?? defaultName;
    }

    private static string ResolveDefaultProviderName(
        IReadOnlyList<ConfiguredProvider> configuredProviders,
        string? preferredDefault)
    {
        var normalizedPreferred = string.IsNullOrWhiteSpace(preferredDefault)
            ? null
            : preferredDefault.Trim();

        if (configuredProviders.Count == 0)
            return normalizedPreferred ?? "openai";

        if (!string.IsNullOrWhiteSpace(normalizedPreferred) &&
            configuredProviders.Any(p => string.Equals(p.Name, normalizedPreferred, StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedPreferred;
        }

        return configuredProviders[0].Name;
    }

    private static MEAILLMProviderFactory BuildMeaiFactory(
        IEnumerable<ConfiguredProvider> configuredProviders,
        string defaultName)
    {
        var factory = new MEAILLMProviderFactory();
        foreach (var provider in configuredProviders)
        {
            factory.RegisterOpenAI(
                provider.Name,
                provider.Model,
                provider.ApiKey,
                string.IsNullOrWhiteSpace(provider.Endpoint) ? null : provider.Endpoint);
        }

        factory.SetDefault(defaultName);
        return factory;
    }

    private static TornadoLLMProviderFactory BuildTornadoFactory(
        IEnumerable<ConfiguredProvider> configuredProviders,
        string defaultName)
    {
        var factory = new TornadoLLMProviderFactory();
        foreach (var provider in configuredProviders)
        {
            factory.RegisterOpenAICompatible(
                provider.Name,
                provider.ApiKey,
                provider.Model,
                string.IsNullOrWhiteSpace(provider.Endpoint) ? null : provider.Endpoint);
        }

        factory.SetDefault(defaultName);
        return factory;
    }

    private static Func<IAevatarSecretsStore> CreateSecretsStoreAccessor(AevatarAIFeatureOptions options)
    {
        if (options.SecretsStore != null)
            return () => options.SecretsStore;

        return static () => new AevatarSecretsStore();
    }

    private static Func<long> BuildProviderConfigVersionProvider(AevatarAIFeatureOptions options)
    {
        if (options.SecretsStore != null)
            return static () => 0L;

        return static () => HashCode.Combine(
            ComputeFileVersion(AevatarPaths.SecretsJson),
            ComputeFileVersion(AevatarPaths.ConfigJson));
    }

    private static long ComputeFileVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return 0L;

            var info = new FileInfo(path);
            return HashCode.Combine(info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch
        {
            return 0L;
        }
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

            var semantic = ResolveProviderSemantic(providerType, name, options.DefaultProvider, options);
            var resolvedModel = string.IsNullOrWhiteSpace(model)
                ? semantic.Model
                : model.Trim();
            var resolvedEndpoint = string.IsNullOrWhiteSpace(endpoint)
                ? semantic.Endpoint
                : endpoint.Trim();

            configured.Add(new ConfiguredProvider(name.Trim(), semantic.ProviderType, resolvedModel, resolvedEndpoint, apiKey.Trim()));
        }

        return configured;
    }

    private static FallbackRegistration? ResolveFallbackRegistration(AevatarAIFeatureOptions options)
    {
        var apiKeySelection = ResolveApiKeySelection(options);
        if (apiKeySelection is null)
            return null;

        ProviderSemantic semantic;
        string providerName;
        switch (apiKeySelection.Source)
        {
            case ApiKeySource.DeepSeekEnvironment:
                semantic = BuildProviderSemantic(ProviderKind.DeepSeek, options);
                providerName = semantic.ProviderType;
                break;
            case ApiKeySource.OpenAIEnvironment:
                semantic = BuildProviderSemantic(ProviderKind.OpenAI, options);
                providerName = semantic.ProviderType;
                break;
            default:
                semantic = ResolveProviderSemantic(null, options.DefaultProvider, null, options);
                providerName = string.IsNullOrWhiteSpace(options.DefaultProvider)
                    ? semantic.ProviderType
                    : options.DefaultProvider.Trim();
                break;
        }

        return new FallbackRegistration(providerName, semantic.Model, semantic.Endpoint, apiKeySelection.ApiKey);
    }

    private static ApiKeySelection? ResolveApiKeySelection(AevatarAIFeatureOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            return new ApiKeySelection(options.ApiKey.Trim(), ApiKeySource.Options);

        var deepSeekApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(deepSeekApiKey))
            return new ApiKeySelection(deepSeekApiKey.Trim(), ApiKeySource.DeepSeekEnvironment);

        var openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(openAiApiKey))
            return new ApiKeySelection(openAiApiKey.Trim(), ApiKeySource.OpenAIEnvironment);

        var genericApiKey = Environment.GetEnvironmentVariable("AEVATAR_LLM_API_KEY");
        if (!string.IsNullOrWhiteSpace(genericApiKey))
            return new ApiKeySelection(genericApiKey.Trim(), ApiKeySource.GenericEnvironment);

        return null;
    }

    private static ProviderSemantic ResolveProviderSemantic(
        string? providerTypeHint,
        string? providerNameHint,
        string? fallbackHint,
        AevatarAIFeatureOptions options)
    {
        if (TryResolveProviderKind(providerTypeHint, out var providerKind))
            return BuildProviderSemantic(providerKind, options);

        if (TryResolveProviderKind(providerNameHint, out providerKind))
            return BuildProviderSemantic(providerKind, options);

        if (TryResolveProviderKind(fallbackHint, out providerKind))
            return BuildProviderSemantic(providerKind, options);

        return BuildProviderSemantic(ProviderKind.OpenAI, options);
    }

    private static bool TryResolveProviderKind(string? candidate, out ProviderKind providerKind)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            if (candidate.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                providerKind = ProviderKind.DeepSeek;
                return true;
            }

            if (candidate.Contains("openai", StringComparison.OrdinalIgnoreCase))
            {
                providerKind = ProviderKind.OpenAI;
                return true;
            }
        }

        providerKind = default;
        return false;
    }

    private static ProviderSemantic BuildProviderSemantic(ProviderKind providerKind, AevatarAIFeatureOptions options)
    {
        return providerKind switch
        {
            ProviderKind.DeepSeek => new ProviderSemantic("deepseek", options.DeepSeekModel, "https://api.deepseek.com/v1"),
            _ => new ProviderSemantic("openai", options.OpenAIModel, null),
        };
    }

    private sealed record FallbackRegistration(
        string ProviderName,
        string Model,
        string? Endpoint,
        string ApiKey);

    private sealed record ApiKeySelection(
        string ApiKey,
        ApiKeySource Source);

    private sealed record ProviderSemantic(
        string ProviderType,
        string Model,
        string? Endpoint);

    private enum ApiKeySource
    {
        Options,
        DeepSeekEnvironment,
        OpenAIEnvironment,
        GenericEnvironment,
    }

    private enum ProviderKind
    {
        OpenAI,
        DeepSeek,
    }

    private sealed record ConfiguredProvider(
        string Name,
        string ProviderType,
        string Model,
        string? Endpoint,
        string ApiKey);

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
