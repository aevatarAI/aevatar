using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Agents;
using Aevatar.AI.Core.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.LLMProviders.NyxId;
using Aevatar.AI.LLMProviders.Tornado;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.Ornn;
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
    public bool EnableOrnnSkills { get; set; }
    public string? OrnnBaseUrl { get; set; }
    public IAevatarSecretsStore? SecretsStore { get; set; }
    public string? ApiKey { get; set; }
    public string? NyxIdGatewayEndpoint { get; set; }
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

        if (options.EnableOrnnSkills)
            RegisterOrnnSkills(services, options);

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
        var configuredProviders = ReadConfiguredProviders(secrets, configuration, options);
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

        var nyxIdProviders = configuredProviders
            .Where(provider => IsNyxIdProviderType(provider.ProviderType))
            .ToList();

        // Auto-register NyxID provider from appsettings when Aevatar:NyxId is configured
        if (nyxIdProviders.Count == 0)
        {
            var autoRegistered = TryAutoRegisterNyxIdProvider(configuration, options);
            if (autoRegistered != null)
            {
                nyxIdProviders.Add(autoRegistered);
                configuredProviders.Add(autoRegistered);
                defaultName = ResolveDefaultProviderName(configuredProviders, preferredDefault);
            }
        }

        if (nyxIdProviders.Count == 0)
            return BuildPrimaryFactory(configuredProviders, defaultName, options);

        var standardProviders = configuredProviders
            .Where(provider => !IsNyxIdProviderType(provider.ProviderType))
            .ToList();
        var nyxIdFactory = BuildNyxIdFactory(nyxIdProviders, defaultName);
        if (standardProviders.Count == 0)
            return nyxIdFactory;

        var primaryFactory = BuildPrimaryFactory(standardProviders, defaultName, options);
        var extraProviders = nyxIdFactory
            .GetAvailableProviders()
            .Select(nyxIdFactory.GetProvider)
            .ToList();
        return new CompositeLLMProviderFactory(primaryFactory, extraProviders, defaultName);
    }

    private static ILLMProviderFactory BuildPrimaryFactory(
        IReadOnlyList<ConfiguredProvider> configuredProviders,
        string defaultName,
        AevatarAIFeatureOptions options)
    {
        var primaryDefaultName = ResolveDefaultProviderName(configuredProviders, defaultName);
        var meaiFactory = BuildMeaiFactory(configuredProviders, primaryDefaultName);
        if (!options.EnableMEAIToTornadoFailover)
            return meaiFactory;

        var tornadoDefaultName = ResolveTornadoDefaultProviderName(configuredProviders, primaryDefaultName, options);
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

    private static NyxIdLLMProviderFactory BuildNyxIdFactory(
        IEnumerable<ConfiguredProvider> configuredProviders,
        string defaultName)
    {
        var factory = new NyxIdLLMProviderFactory();
        foreach (var provider in configuredProviders)
        {
            if (string.IsNullOrWhiteSpace(provider.Endpoint))
            {
                throw new InvalidOperationException(
                    $"NyxID provider '{provider.Name}' requires a gateway endpoint. " +
                    $"Configure LLMProviders:Providers:{provider.Name}:Endpoint or set Aevatar:NyxId:Authority.");
            }

            factory.RegisterGateway(
                provider.Name,
                provider.Model,
                provider.Endpoint,
                // NyxID gateway token comes exclusively from per-request metadata
                // (the caller's Bearer token). No local secrets fallback.
                static () => null);
        }

        factory.SetDefault(ResolveDefaultProviderName(configuredProviders.ToList(), defaultName));
        return factory;
    }

    private static ConfiguredProvider? TryAutoRegisterNyxIdProvider(
        IConfiguration configuration,
        AevatarAIFeatureOptions options)
    {
        var gatewayEndpoint = ResolveNyxIdGatewayEndpoint(configuration, options);
        if (string.IsNullOrWhiteSpace(gatewayEndpoint))
            return null;

        var model = configuration["Aevatar:NyxId:DefaultModel"] ?? options.OpenAIModel;
        return new ConfiguredProvider("nyxid", "nyxid", model, gatewayEndpoint, string.Empty);
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
        IConfiguration configuration,
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

            var semantic = ResolveProviderSemantic(configuration, providerType, name, options.DefaultProvider, options);
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
                semantic = BuildProviderSemantic(new ConfigurationBuilder().Build(), ProviderKind.DeepSeek, options);
                providerName = semantic.ProviderType;
                break;
            case ApiKeySource.OpenAIEnvironment:
                semantic = BuildProviderSemantic(new ConfigurationBuilder().Build(), ProviderKind.OpenAI, options);
                providerName = semantic.ProviderType;
                break;
            default:
                semantic = ResolveProviderSemantic(new ConfigurationBuilder().Build(), null, options.DefaultProvider, null, options);
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
        IConfiguration configuration,
        string? providerTypeHint,
        string? providerNameHint,
        string? fallbackHint,
        AevatarAIFeatureOptions options)
    {
        if (TryResolveProviderKind(providerTypeHint, out var providerKind))
            return BuildProviderSemantic(configuration, providerKind, options);

        if (TryResolveProviderKind(providerNameHint, out providerKind))
            return BuildProviderSemantic(configuration, providerKind, options);

        if (TryResolveProviderKind(fallbackHint, out providerKind))
            return BuildProviderSemantic(configuration, providerKind, options);

        return BuildProviderSemantic(configuration, ProviderKind.OpenAI, options);
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

            if (candidate.Contains("nyxid", StringComparison.OrdinalIgnoreCase))
            {
                providerKind = ProviderKind.NyxId;
                return true;
            }
        }

        providerKind = default;
        return false;
    }

    private static ProviderSemantic BuildProviderSemantic(
        IConfiguration configuration,
        ProviderKind providerKind,
        AevatarAIFeatureOptions options)
    {
        return providerKind switch
        {
            ProviderKind.DeepSeek => new ProviderSemantic("deepseek", options.DeepSeekModel, "https://api.deepseek.com/v1"),
            ProviderKind.NyxId => new ProviderSemantic("nyxid", options.OpenAIModel, ResolveNyxIdGatewayEndpoint(configuration, options)),
            _ => new ProviderSemantic("openai", options.OpenAIModel, null),
        };
    }

    private static string? ResolveNyxIdGatewayEndpoint(IConfiguration configuration, AevatarAIFeatureOptions options)
    {
        var configuredEndpoint = options.NyxIdGatewayEndpoint;
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
            return NormalizeNyxIdGatewayEndpoint(configuredEndpoint);

        configuredEndpoint = configuration["Aevatar:NyxId:GatewayEndpoint"]
            ?? configuration["Cli:App:NyxId:GatewayEndpoint"];
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
            return NormalizeNyxIdGatewayEndpoint(configuredEndpoint);

        var authority = configuration["Cli:App:NyxId:Authority"]
            ?? configuration["Aevatar:NyxId:Authority"]
            ?? configuration["Aevatar:Authentication:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
            return null;

        return NormalizeNyxIdGatewayEndpoint(authority);
    }

    private static string? NormalizeNyxIdGatewayEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;

        var absolute = uri.ToString().TrimEnd('/');
        if (absolute.EndsWith("/api/v1/llm/gateway/v1", StringComparison.OrdinalIgnoreCase))
            return absolute;

        return $"{absolute}/api/v1/llm/gateway/v1";
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
        NyxId,
    }

    private sealed record ConfiguredProvider(
        string Name,
        string ProviderType,
        string Model,
        string? Endpoint,
        string ApiKey);

    private static bool IsNyxIdProviderType(string providerType) =>
        providerType.Contains("nyxid", StringComparison.OrdinalIgnoreCase);

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

    private static void RegisterOrnnSkills(IServiceCollection services, AevatarAIFeatureOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OrnnBaseUrl))
            return;

        services.AddOrnnSkills(o => o.BaseUrl = options.OrnnBaseUrl);
    }
}
