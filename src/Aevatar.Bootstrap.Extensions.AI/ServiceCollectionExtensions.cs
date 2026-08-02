using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Auditing;
using Aevatar.AI.Core.DependencyInjection;
using Aevatar.AI.Core.Voice;
using Aevatar.AI.Core.LLMProviders;
using Aevatar.AI.LLMProviders.MEAI;
using Aevatar.AI.LLMProviders.NyxId;
using Aevatar.AI.LLMProviders.Tornado;
using Aevatar.AI.ToolProviders.MCP;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Scripting;
using Aevatar.AI.ToolProviders.ServiceInvoke;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.AI.ToolProviders.Binding;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.AI.ToolProviders.Workflow;
using Aevatar.AI.ToolProviders.Workflow.Ports;
using Aevatar.Bootstrap.Extensions.AI.OrnnPublishing;
using Aevatar.AI.Infrastructure.Local.Adapters;
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Extensions.AI.Connectors;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Integration.AI;
using Aevatar.Configuration;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.MiniCPM;
using Aevatar.Foundation.VoicePresence.Modules;
using Aevatar.Foundation.VoicePresence.OpenAI;
using Aevatar.Foundation.VoicePresence.Projection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Bootstrap.Extensions.AI;

public sealed class AevatarAIFeatureOptions
{
    public int MaxTurnDeadlineMs { get; set; } = RoleChatExecutionOptions.DefaultMaxTurnDeadlineMs;
    public int PostCommitConfigRefreshTimeoutMs { get; set; } =
        RoleChatExecutionOptions.DefaultPostCommitConfigRefreshTimeoutMs;
    public int PostTurnProcessingTimeoutMs { get; set; } =
        RoleChatExecutionOptions.DefaultPostTurnProcessingTimeoutMs;
    public bool EnableMEAIProviders { get; set; } = true;
    public bool EnableMEAIToTornadoFailover { get; set; } = true;
    public bool EnableReloadableProviderFactory { get; set; }
    public bool FailoverFallbackToDefaultProviderWhenNamedProviderMissing { get; set; } = true;
    public bool FailoverPreferFallbackDefaultProvider { get; set; } = true;
    public bool FailoverPreferDeepSeekAsFallbackDefault { get; set; } = true;
    public bool EnableMCPTools { get; set; }
    public bool EnableSkills { get; set; }
    public bool EnableOrnnSkills { get; set; }
    /// <summary>
    /// Optional override for the NyxID-bound slug pointing at the Ornn skill API. Defaults to
    /// chrono-ornn's canonical <c>"ornn"</c> when null/empty. Override only if the deployment's
    /// NyxID catalog uses a different slug (e.g. organisations that re-registered the service).
    /// </summary>
    public string? OrnnNyxIdSlug { get; set; }
    /// <summary>
    /// Enables the host-bound system skill overlay scaffold. Mainnet-style hosts bind this from
    /// <c>Aevatar:SystemSkills:Enabled</c>.
    /// </summary>
    public bool EnableSystemSkillOverlay { get; set; }
    /// <summary>
    /// Non-secret name of the public, org-owned Ornn skillset that sources the overlay (issue #2498).
    /// Bound from <c>Aevatar:SystemSkills:SetName</c>. No organization service token is needed: set
    /// ownership is the trust anchor and the set is read publicly through the existing ornn-api proxy.
    /// </summary>
    public string? SystemSkillOverlaySetName { get; set; }
    /// <summary>
    /// Bound from <c>Aevatar:SystemSkills:RefreshTtl</c>.
    /// </summary>
    public TimeSpan SystemSkillOverlayRefreshTtl { get; set; }
    /// <summary>
    /// Bound from <c>Aevatar:SystemSkills:MaxSkills</c>.
    /// </summary>
    public int SystemSkillOverlayMaxSkills { get; set; }
    /// <summary>
    /// Bound from <c>Aevatar:SystemSkills:MaxBytes</c>.
    /// </summary>
    public int SystemSkillOverlayMaxBytes { get; set; }
    public IAevatarSecretsStore? SecretsStore { get; set; }
    public string? ApiKey { get; set; }
    public NyxIdLlmEndpointSpec? NyxIdLlmEndpoint { get; set; }
    public string DefaultProvider { get; set; } = "openai";
    public string OpenAIModel { get; set; } = LlmDefaults.Model;
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public List<string> SkillDirectories { get; } = [];
    public bool EnableServiceInvokeTools { get; set; }
    public string? ServiceInvokeTenantId { get; set; }
    public string? ServiceInvokeAppId { get; set; }
    public string? ServiceInvokeNamespace { get; set; }
    public bool BypassServiceInvokeApproval { get; set; }
    public bool EnableWebTools { get; set; }
    public string? WebSearchNyxIdSlug { get; set; }
    public string? WebSearchApiBaseUrl { get; set; }
    public bool EnableWorkflowTools { get; set; }
    public bool EnableScriptingTools { get; set; }
    public bool EnableBindingTools { get; set; }
    public VoicePresenceFeatureOptions VoicePresence { get; } = new();
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
        options.MaxTurnDeadlineMs = ReadPositiveInteger(
            configuration,
            "Aevatar:AI:MaxTurnDeadlineMs",
            options.MaxTurnDeadlineMs);
        options.PostCommitConfigRefreshTimeoutMs = ReadPositiveInteger(
            configuration,
            "Aevatar:AI:PostCommitConfigRefreshTimeoutMs",
            options.PostCommitConfigRefreshTimeoutMs);
        options.PostTurnProcessingTimeoutMs = ReadPositiveInteger(
            configuration,
            "Aevatar:AI:PostTurnProcessingTimeoutMs",
            options.PostTurnProcessingTimeoutMs);
        configure?.Invoke(options);
        services.TryAddSingleton(new RoleChatExecutionOptions(
            options.MaxTurnDeadlineMs,
            options.PostCommitConfigRefreshTimeoutMs,
            options.PostTurnProcessingTimeoutMs));

        services.AddAevatarAgentKindRegistry(builder => builder
            .ScanAssemblies(typeof(RoleGAgent).Assembly)
            .Register<WorkflowRoleGAgent>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowToolSource, AgentWorkflowToolSourceAdapter>());
        services.AddAgentToolExecution();
        services.TryAddSingleton<IVoiceToolInvoker>(sp => new AgentToolVoiceInvoker(
            sp.GetServices<IAgentToolSource>(),
            sp.GetRequiredService<IAgentToolExecutionPort>(),
            ResolveVoiceCredentialProviders(sp),
            sp.GetService<ILogger<AgentToolVoiceInvoker>>()));
        services.TryAddSingleton<IVoiceToolCatalog>(sp => new AgentToolVoiceCatalog(
            sp.GetServices<IAgentToolSource>(),
            ResolveVoiceCredentialProviders(sp),
            sp.GetService<ILogger<AgentToolVoiceCatalog>>()));
        services.TryAddSingleton<IVoicePresenceCapabilityCommandPort, VoicePresenceCapabilityCommandPort>();
        // Zero-config /ws/voice: auto-provision a never-enabled default voice agent on first connect by
        // committing the same enable voice-presence/enable issues. The attach path (ActorOwnedVoiceRealtimeSession)
        // resolves this optionally and only invokes it when the capability is still null after the
        // re-projection self-heal — i.e. a genuinely-unprovisioned default agent.
        services.TryAddSingleton<IVoicePresenceCapabilityAutoEnablePort, VoicePresenceCapabilityAutoEnablePort>();
        services.TryAddSingleton<IWorkflowYamlValidator, WorkflowYamlValidatorImpl>();
        services.TryAddSingleton<IWorkflowDefinitionCommandAdapter>(sp =>
            new LocalWorkflowDefinitionCommandAdapter(
                sp.GetRequiredService<IWorkflowYamlValidator>(),
                workflowsDirectory: null,
                sp.GetService<ILogger<LocalWorkflowDefinitionCommandAdapter>>()));
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

        if (options.EnableSystemSkillOverlay)
            RegisterSystemSkillOverlay(services, options);

        if (options.EnableServiceInvokeTools)
            RegisterServiceInvokeTools(services, options);

        if (options.EnableWebTools)
            RegisterWebTools(services, options);

        if (options.EnableWorkflowTools)
            RegisterWorkflowTools(services);

        if (options.EnableScriptingTools)
            RegisterScriptingTools(services);

        if (options.EnableBindingTools)
            RegisterBindingTools(services);

        RegisterVoicePresenceModules(services, configuration, options);

        return services;
    }

    private static int ReadPositiveInteger(
        IConfiguration configuration,
        string key,
        int defaultValue)
    {
        var configuredValue = configuration[key];
        if (string.IsNullOrWhiteSpace(configuredValue))
            return defaultValue;

        if (!int.TryParse(configuredValue, out var value))
            throw new InvalidOperationException($"{key} must be a positive integer.");

        return value;
    }

    private static void RegisterVoicePresenceModules(
        IServiceCollection services,
        IConfiguration configuration,
        AevatarAIFeatureOptions options)
    {
        var voiceOptions = options.VoicePresence;
        if (!voiceOptions.EnableModuleFactory)
            return;

        var registrations = BuildVoicePresenceModuleRegistrations(configuration, options);
        if (registrations.Count == 0)
            return;

        // Refactor (cluster-voice-nyxid-ephemeral-broker): when the OpenAI realtime credential is
        // brokered through NyxID, register the resolver so the provider mints a per-session ephemeral
        // instead of reading a static OPENAI_API_KEY. Requires NyxID tools (INyxIdApiClientFactory) wired.
        var nyxIdRealtimeCredentialOptions = BuildNyxIdRealtimeCredentialOptions(configuration);
        if (nyxIdRealtimeCredentialOptions.Enabled)
        {
            services.TryAddSingleton(nyxIdRealtimeCredentialOptions);
            services.TryAddSingleton<IRealtimeProviderCredentialResolver>(sp => new NyxIdRealtimeProviderCredentialResolver(
                sp.GetRequiredService<INyxIdApiClientFactory>(),
                sp.GetRequiredService<NyxIdRealtimeProviderCredentialOptions>(),
                sp.GetRequiredService<ILogger<NyxIdRealtimeProviderCredentialResolver>>(),
                ResolveVoiceCredentialProviders(sp)));
        }

        services.TryAddSingleton<IVoicePresenceCapabilityQueryPort, VoicePresenceCapabilityQueryPort>();
        services.TryAddSingleton<IVoicePresenceLeaseObservationPort, VoicePresenceLeaseObservationPort>();
        services.TryAddSingleton<IVoicePresenceSessionLeasePort, VoicePresenceSessionLeasePort>();
        services.TryAddSingleton<IVoicePresenceTransportAttachmentPort, VoicePresenceTransportAttachmentPort>();
        services.TryAddSingleton(sp => new VoiceVolatileToolCredentialPort(sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IVoiceVolatileToolCredentialPort>(sp => sp.GetRequiredService<VoiceVolatileToolCredentialPort>());
        services.TryAddSingleton<IVoiceToolCredentialIssuer>(sp => sp.GetRequiredService<VoiceVolatileToolCredentialPort>());
        services.AddOptions<VoiceWebSocketAttachOptions>();
        services.TryAddSingleton<IVoiceVolatileMediaStreamPort, VoiceVolatileMediaStreamPort>();
        services.TryAddSingleton<IValidateOptions<VoiceWebSocketAttachOptions>, VoiceWebSocketAttachOptionsValidator>();
        services.TryAddSingleton<VoiceWebSocketAttachExecutor>();
        services.AddVoiceWebRtcTransport();
        services.TryAddSingleton<IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>, ActorOwnedVoiceRealtimeSession>();
        services.AddVoicePresenceCapabilityProjection();
        services.AddVoicePresenceCapabilityProjectionStore(configuration);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IEventModuleFactory<IEventHandlerContext>, VoicePresenceModuleFactory>());
        foreach (var registration in registrations)
            services.AddSingleton(registration);
    }

    private static IReadOnlyList<ICredentialProvider> ResolveVoiceCredentialProviders(IServiceProvider services)
    {
        var providers = new List<ICredentialProvider>();
        var voiceCredentialProvider = services.GetService<IVoiceToolCredentialIssuer>() as ICredentialProvider;
        if (voiceCredentialProvider is not null)
            providers.Add(voiceCredentialProvider);

        foreach (var provider in services.GetServices<ICredentialProvider>())
        {
            if (!ReferenceEquals(provider, voiceCredentialProvider))
                providers.Add(provider);
        }

        return providers;
    }

    private static IServiceCollection AddVoicePresenceCapabilityProjectionStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var documentProvider = ProjectionDocumentProviderConfiguration.Resolve(configuration, "VoicePresence");

        if (HasAnyVoicePresenceCapabilityReader(services))
            return services;

        if (documentProvider.ElasticsearchEnabled)
        {
            services.AddElasticsearchDocumentProjectionStore<VoicePresenceCapabilityReadModel, string>(
                optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<VoicePresenceCapabilityReadModel>>().Metadata,
                keySelector: static readModel => readModel.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<VoicePresenceCapabilityReadModel, string>(
                keySelector: static readModel => readModel.Id,
                keyFormatter: static key => key,
                defaultSortSelector: static readModel => readModel.UpdatedAt);
        }

        return services;
    }

    private static bool HasAnyVoicePresenceCapabilityReader(IServiceCollection services)
    {
        return services.Any(x =>
            x.ServiceType == typeof(IProjectionDocumentReader<VoicePresenceCapabilityReadModel, string>));
    }

    private static List<VoicePresenceModuleRegistration> BuildVoicePresenceModuleRegistrations(
        IConfiguration configuration,
        AevatarAIFeatureOptions options)
    {
        var registrations = new List<VoicePresenceModuleRegistration>();
        var voiceOptions = options.VoicePresence;
        var openAIProviderConfig = BuildOpenAIVoiceProviderConfig(configuration, options);
        var miniCpmProviderConfig = BuildMiniCpmVoiceProviderConfig(configuration, options);
        var nyxIdRealtimeBrokerEnabled = IsNyxIdRealtimeBrokerEnabled(configuration);
        // The openai module registers below when EITHER a raw ApiKey OR the NyxID ephemeral broker is
        // available (ADR-0033 forbids long-lived keys in production, so broker-only is the normal shape).
        // Default-provider resolution must use the same availability, or broker-only deployments register
        // the module solely as "voice_presence_openai" while the auto-enable/mount/read default path uses
        // "voice_presence" — the module factory then never mounts a module for the enabled name, the
        // session-lease signal is silently dropped, and /ws/voice loops on 503 voice_capability_not_ready.
        var openAIVoiceAvailable = IsOpenAIVoiceConfigured(openAIProviderConfig) || nyxIdRealtimeBrokerEnabled;
        var resolvedDefaultProvider = ResolveVoicePresenceDefaultProvider(
            voiceOptions.DefaultProvider,
            openAIVoiceAvailable,
            miniCpmProviderConfig);

        if (openAIVoiceAvailable)
        {
            registrations.Add(new VoicePresenceModuleRegistration(
                BuildVoicePresenceModuleNames(
                    providerName: "openai",
                    isDefaultProvider: string.Equals(resolvedDefaultProvider, "openai", StringComparison.OrdinalIgnoreCase),
                    providerAliases: ["voice_presence_openai"]),
                (serviceProvider, resolvedModuleName) => new VoicePresenceModule(
                    new OpenAIRealtimeProvider(
                        voiceOptions.OpenAIProviderOptions,
                        serviceProvider.GetService<ILogger<OpenAIRealtimeProvider>>(),
                        serviceProvider.GetService<IRealtimeProviderCredentialResolver>()),
                    openAIProviderConfig.Clone(),
                    BuildOpenAIVoiceSessionConfig(configuration, options),
                    CloneVoicePresenceModuleOptions(voiceOptions.Module, resolvedModuleName),
                    serviceProvider.GetService<IVoiceToolInvoker>(),
                    serviceProvider.GetService<IVoiceToolCatalog>(),
                    serviceProvider.GetService<ILogger<VoicePresenceModule>>()),
                (serviceProvider, handle, eventSink, audioSink, ct) => ConnectVoiceProviderSessionWithLoggingAsync(
                    handle,
                    new OpenAIRealtimeProvider(
                        voiceOptions.OpenAIProviderOptions,
                        serviceProvider.GetService<ILogger<OpenAIRealtimeProvider>>(),
                        serviceProvider.GetService<IRealtimeProviderCredentialResolver>()),
                    openAIProviderConfig.Clone(),
                    BuildOpenAIVoiceSessionConfig(configuration, options),
                    serviceProvider.GetService<IVoiceToolCatalog>(),
                    eventSink,
                    audioSink,
                    serviceProvider.GetService<ILogger<ReadinessGatedRealtimeVoiceProviderSession>>(),
                    ct),
                BuildOpenAIVoiceSessionConfig(configuration, options).SampleRateHz));
        }

        if (IsMiniCpmVoiceConfigured(miniCpmProviderConfig))
        {
            registrations.Add(new VoicePresenceModuleRegistration(
                BuildVoicePresenceModuleNames(
                    providerName: "minicpm",
                    isDefaultProvider: string.Equals(resolvedDefaultProvider, "minicpm", StringComparison.OrdinalIgnoreCase),
                    providerAliases: ["voice_presence_minicpm", "voice_presence_minicpm_o"]),
                (serviceProvider, resolvedModuleName) => new VoicePresenceModule(
                    new MiniCPMRealtimeProvider(
                        voiceOptions.MiniCPMProviderOptions,
                        serviceProvider.GetService<ILogger<MiniCPMRealtimeProvider>>()),
                    miniCpmProviderConfig.Clone(),
                    BuildMiniCpmVoiceSessionConfig(configuration, options),
                    CloneVoicePresenceModuleOptions(voiceOptions.Module, resolvedModuleName),
                    serviceProvider.GetService<IVoiceToolInvoker>(),
                    serviceProvider.GetService<IVoiceToolCatalog>(),
                    serviceProvider.GetService<ILogger<VoicePresenceModule>>()),
                (serviceProvider, handle, eventSink, audioSink, ct) => ConnectVoiceProviderSessionWithLoggingAsync(
                    handle,
                    new MiniCPMRealtimeProvider(
                        voiceOptions.MiniCPMProviderOptions,
                        serviceProvider.GetService<ILogger<MiniCPMRealtimeProvider>>()),
                    miniCpmProviderConfig.Clone(),
                    BuildMiniCpmVoiceSessionConfig(configuration, options),
                    serviceProvider.GetService<IVoiceToolCatalog>(),
                    eventSink,
                    audioSink,
                    serviceProvider.GetService<ILogger<ReadinessGatedRealtimeVoiceProviderSession>>(),
                    ct),
                BuildMiniCpmVoiceSessionConfig(configuration, options).SampleRateHz));
        }

        return registrations;
    }

    private static async Task<RealtimeVoiceProviderSession> ConnectVoiceProviderSessionAsync(
        VoicePresenceSessionLeaseHandle handle,
        IRealtimeVoiceProvider provider,
        VoiceProviderConfig providerConfig,
        VoiceSessionConfig sessionConfig,
        IVoiceToolCatalog? toolCatalog,
        Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
        Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
        CancellationToken ct)
    {
        return await ConnectVoiceProviderSessionWithLoggingAsync(
            handle,
            provider,
            providerConfig,
            sessionConfig,
            toolCatalog,
            eventSink,
            audioSink,
            logger: null,
            ct);
    }

    private static async Task<RealtimeVoiceProviderSession> ConnectVoiceProviderSessionWithLoggingAsync(
        VoicePresenceSessionLeaseHandle handle,
        IRealtimeVoiceProvider provider,
        VoiceProviderConfig providerConfig,
        VoiceSessionConfig sessionConfig,
        IVoiceToolCatalog? toolCatalog,
        Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
        Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
        ILogger<ReadinessGatedRealtimeVoiceProviderSession>? logger,
        CancellationToken ct)
    {
        return await VoiceRealtimeSessionReadinessBootstrapper.ConnectAsync(
            handle,
            provider,
            providerConfig,
            sessionConfig,
            toolCatalog,
            eventSink,
            audioSink,
            logger,
            ct);
    }

    private static string? ResolveVoicePresenceDefaultProvider(
        string? requestedProvider,
        bool openAIVoiceAvailable,
        VoiceProviderConfig miniCpmProviderConfig)
    {
        var normalizedRequested = NormalizeVoicePresenceProviderName(requestedProvider);
        if (string.Equals(normalizedRequested, "openai", StringComparison.OrdinalIgnoreCase) &&
            openAIVoiceAvailable)
        {
            return "openai";
        }

        if (string.Equals(normalizedRequested, "minicpm", StringComparison.OrdinalIgnoreCase) &&
            IsMiniCpmVoiceConfigured(miniCpmProviderConfig))
        {
            return "minicpm";
        }

        if (openAIVoiceAvailable)
            return "openai";

        if (IsMiniCpmVoiceConfigured(miniCpmProviderConfig))
            return "minicpm";

        return null;
    }

    private static string[] BuildVoicePresenceModuleNames(
        string providerName,
        bool isDefaultProvider,
        IEnumerable<string> providerAliases)
    {
        var names = new List<string>();
        if (isDefaultProvider)
            names.Add("voice_presence");

        names.AddRange(providerAliases);
        if (!names.Contains(providerName, StringComparer.OrdinalIgnoreCase))
            names.Add(providerName == "openai" ? "voice_presence_openai" : "voice_presence_minicpm");

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Refactor (cluster-voice-nyxid-ephemeral-broker): the broker slug defaults to
    // NyxIdRealtimeProviderCredentialOptions.DefaultServiceSlug so voice stays enabled even when the
    // deployment config/secret is wiped on redeploy — config only overrides the slug, never disables it.
    private static string ResolveNyxIdRealtimeServiceSlug(IConfiguration configuration)
    {
        var configured = configuration["Aevatar:VoicePresence:OpenAI:Nyxid:ServiceSlug"]?.Trim();
        return string.IsNullOrWhiteSpace(configured)
            ? NyxIdRealtimeProviderCredentialOptions.DefaultServiceSlug
            : configured;
    }

    private static bool IsNyxIdRealtimeBrokerEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(ResolveNyxIdRealtimeServiceSlug(configuration));

    private static NyxIdRealtimeProviderCredentialOptions BuildNyxIdRealtimeCredentialOptions(
        IConfiguration configuration)
    {
        var options = new NyxIdRealtimeProviderCredentialOptions
        {
            ServiceSlug = ResolveNyxIdRealtimeServiceSlug(configuration),
        };

        var mintPath = configuration["Aevatar:VoicePresence:OpenAI:Nyxid:MintPath"];
        if (!string.IsNullOrWhiteSpace(mintPath))
            options.MintPath = mintPath.Trim();

        var model = configuration["Aevatar:VoicePresence:OpenAI:Nyxid:Model"];
        if (!string.IsNullOrWhiteSpace(model))
            options.Model = model.Trim();

        return options;
    }

    private static VoiceProviderConfig BuildOpenAIVoiceProviderConfig(
        IConfiguration configuration,
        AevatarAIFeatureOptions options)
    {
        var config = options.VoicePresence.OpenAIProvider.Clone();
        config.ProviderName = FirstNonEmpty(config.ProviderName, "openai")!;
        AssignIfNonEmpty(config, static (target, value) => target.ApiKey = value, FirstNonEmpty(
            config.ApiKey,
            configuration["Aevatar:VoicePresence:OpenAI:ApiKey"],
            Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            options.ApiKey));
        AssignIfNonEmpty(config, static (target, value) => target.Endpoint = value, FirstNonEmpty(
            config.Endpoint,
            configuration["Aevatar:VoicePresence:OpenAI:Endpoint"]));
        config.Model = FirstNonEmpty(
            config.Model,
            configuration["Aevatar:VoicePresence:OpenAI:Model"],
            OpenAIRealtimeProviderOptions.DefaultModelName)!;
        return config;
    }

    private static VoiceSessionConfig BuildOpenAIVoiceSessionConfig(
        IConfiguration configuration,
        AevatarAIFeatureOptions options)
    {
        var session = options.VoicePresence.OpenAISession.Clone();
        session.Voice = FirstNonEmpty(session.Voice, configuration["Aevatar:VoicePresence:OpenAI:Voice"]) ?? string.Empty;
        session.Instructions = FirstNonEmpty(
            session.Instructions,
            configuration["Aevatar:VoicePresence:OpenAI:Instructions"]) ?? string.Empty;
        if (session.SampleRateHz == 0)
            session.SampleRateHz = OpenAIRealtimeProviderOptions.DefaultSampleRateHz;
        return session;
    }

    private static VoiceProviderConfig BuildMiniCpmVoiceProviderConfig(
        IConfiguration configuration,
        AevatarAIFeatureOptions options)
    {
        var config = options.VoicePresence.MiniCPMProvider.Clone();
        config.ProviderName = FirstNonEmpty(config.ProviderName, "minicpm")!;
        AssignIfNonEmpty(config, static (target, value) => target.ApiKey = value, FirstNonEmpty(
            config.ApiKey,
            configuration["Aevatar:VoicePresence:MiniCPM:ApiKey"]));
        AssignIfNonEmpty(config, static (target, value) => target.Endpoint = value, FirstNonEmpty(
            config.Endpoint,
            configuration["Aevatar:VoicePresence:MiniCPM:Endpoint"]));
        config.Model = FirstNonEmpty(
            config.Model,
            configuration["Aevatar:VoicePresence:MiniCPM:Model"],
            "minicpm-o")!;
        return config;
    }

    private static VoiceSessionConfig BuildMiniCpmVoiceSessionConfig(
        IConfiguration configuration,
        AevatarAIFeatureOptions options)
    {
        var session = options.VoicePresence.MiniCPMSession.Clone();
        session.Voice = FirstNonEmpty(session.Voice, configuration["Aevatar:VoicePresence:MiniCPM:Voice"]) ?? string.Empty;
        session.Instructions = FirstNonEmpty(
            session.Instructions,
            configuration["Aevatar:VoicePresence:MiniCPM:Instructions"]) ?? string.Empty;
        if (session.SampleRateHz == 0)
            session.SampleRateHz = MiniCPMRealtimeProviderOptions.DefaultInputSampleRateHz;
        return session;
    }

    private static VoicePresenceModuleOptions CloneVoicePresenceModuleOptions(
        VoicePresenceModuleOptions options,
        string? resolvedName = null) =>
        new()
        {
            Name = FirstNonEmpty(resolvedName, options.Name) ?? "voice_presence",
            Priority = options.Priority,
            LinkId = options.LinkId,
            StaleAfter = options.StaleAfter,
            DedupeWindow = options.DedupeWindow,
            ToolExecutionTimeout = options.ToolExecutionTimeout,
            DrainTimeout = options.DrainTimeout,
            PendingInjectionCapacity = options.PendingInjectionCapacity,
            TimeProvider = options.TimeProvider,
            DirectExternalEventTypeUrls = options.DirectExternalEventTypeUrls,
            DirectExternalEventNoActiveSessionPolicy = options.DirectExternalEventNoActiveSessionPolicy,
        };

    private static bool IsOpenAIVoiceConfigured(VoiceProviderConfig config) =>
        !string.IsNullOrWhiteSpace(config.ApiKey);

    private static bool IsMiniCpmVoiceConfigured(VoiceProviderConfig config) =>
        !string.IsNullOrWhiteSpace(config.Endpoint);

    private static string? NormalizeVoicePresenceProviderName(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return null;

        return providerName.Trim().ToLowerInvariant() switch
        {
            "minicpm" => "minicpm",
            "minicpm-o" => "minicpm",
            "openai" => "openai",
            _ => null,
        };
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }

        return null;
    }

    private static void AssignIfNonEmpty<T>(
        T target,
        Action<T, string> assign,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            assign(target, value);
    }

    private static void RegisterMeaiProviders(
        IServiceCollection services,
        IConfiguration configuration,
        AevatarAIFeatureOptions options)
    {
        if (!options.EnableMEAIProviders)
            return;

        if (options.EnableReloadableProviderFactory)
        {
            var versionProvider = BuildProviderConfigVersionProvider(options);
            services.TryAddSingleton<ILLMProviderFactory>(sp =>
            {
                var secretsStoreAccessor = CreateSecretsStoreAccessor(options, sp);
                var logger = sp.GetService<ILogger<ReloadableLLMProviderFactory>>();
                var loggerFactory = sp.GetService<ILoggerFactory>();
                var toolExecutionPort = new ServiceProviderAgentToolExecutionPort(sp);
                return new ReloadableLLMProviderFactory(
                    () => BuildLlmProviderFactory(configuration, options, secretsStoreAccessor, toolExecutionPort, loggerFactory),
                    versionProvider,
                    logger);
            });
            return;
        }

        services.TryAddSingleton<ILLMProviderFactory>(sp =>
        {
            var secretsStoreAccessor = CreateSecretsStoreAccessor(options, sp);
            return BuildLlmProviderFactory(
                configuration,
                options,
                secretsStoreAccessor,
                new ServiceProviderAgentToolExecutionPort(sp),
                sp.GetService<ILoggerFactory>());
        });
    }

    private static ILLMProviderFactory BuildLlmProviderFactory(
        IConfiguration configuration,
        AevatarAIFeatureOptions options,
        Func<IAevatarSecretsStore> secretsStoreAccessor,
        IAgentToolExecutionPort toolExecutionPort,
        ILoggerFactory? loggerFactory = null)
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
            return BuildPrimaryFactory(configuredProviders, defaultName, options, toolExecutionPort, loggerFactory);

        var standardProviders = configuredProviders
            .Where(provider => !IsNyxIdProviderType(provider.ProviderType))
            .ToList();
        var nyxIdFactory = BuildNyxIdFactory(nyxIdProviders, defaultName, toolExecutionPort, loggerFactory);
        if (standardProviders.Count == 0)
            return nyxIdFactory;

        var primaryFactory = BuildPrimaryFactory(standardProviders, defaultName, options, toolExecutionPort, loggerFactory);
        var extraProviders = nyxIdFactory
            .GetAvailableProviders()
            .Select(nyxIdFactory.GetProvider)
            .ToList();
        return new CompositeLLMProviderFactory(primaryFactory, extraProviders, defaultName);
    }

    private static ILLMProviderFactory BuildPrimaryFactory(
        IReadOnlyList<ConfiguredProvider> configuredProviders,
        string defaultName,
        AevatarAIFeatureOptions options,
        IAgentToolExecutionPort toolExecutionPort,
        ILoggerFactory? loggerFactory = null)
    {
        var primaryDefaultName = ResolveDefaultProviderName(configuredProviders, defaultName);
        var meaiFactory = BuildMeaiFactory(configuredProviders, primaryDefaultName, toolExecutionPort, loggerFactory);
        if (!options.EnableMEAIToTornadoFailover)
            return meaiFactory;

        var tornadoDefaultName = ResolveTornadoDefaultProviderName(configuredProviders, primaryDefaultName, options);
        var tornadoFactory = BuildTornadoFactory(configuredProviders, tornadoDefaultName, loggerFactory);
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
        string defaultName,
        IAgentToolExecutionPort toolExecutionPort,
        ILoggerFactory? loggerFactory = null)
    {
        var factory = new MEAILLMProviderFactory(toolExecutionPort);
        var providerLogger = loggerFactory?.CreateLogger<MEAILLMProvider>();
        foreach (var provider in configuredProviders)
        {
            factory.RegisterOpenAI(
                provider.Name,
                provider.Model,
                provider.ApiKey,
                string.IsNullOrWhiteSpace(provider.Endpoint) ? null : provider.Endpoint,
                providerLogger);
        }

        factory.SetDefault(defaultName);
        return factory;
    }

    private static TornadoLLMProviderFactory BuildTornadoFactory(
        IEnumerable<ConfiguredProvider> configuredProviders,
        string defaultName,
        ILoggerFactory? loggerFactory = null)
    {
        var factory = new TornadoLLMProviderFactory();
        var providerLogger = loggerFactory?.CreateLogger<TornadoLLMProvider>();
        foreach (var provider in configuredProviders)
        {
            factory.RegisterOpenAICompatible(
                provider.Name,
                provider.ApiKey,
                provider.Model,
                string.IsNullOrWhiteSpace(provider.Endpoint) ? null : provider.Endpoint,
                providerLogger);
        }

        factory.SetDefault(defaultName);
        return factory;
    }

    private static NyxIdLLMProviderFactory BuildNyxIdFactory(
        IEnumerable<ConfiguredProvider> configuredProviders,
        string defaultName,
        IAgentToolExecutionPort toolExecutionPort,
        ILoggerFactory? loggerFactory = null)
    {
        var factory = new NyxIdLLMProviderFactory(toolExecutionPort);
        // Without an explicit logger the provider chain (NyxIdLLMProvider and the
        // MEAILLMProvider it delegates to) falls back to NullLogger, which silences
        // upstream LLM error translations and the no-chunks streaming fallback in
        // production. Always wire the host logger when one is available.
        var providerLogger = loggerFactory?.CreateLogger<NyxIdLLMProvider>();
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
                static () => null,
                provider.DefaultRoutePreference,
                providerLogger);
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

        var model = ResolveNyxIdDefaultModel(configuration, options);
        var defaultRoute = ResolveNyxIdDefaultRoute(configuration);
        return new ConfiguredProvider("nyxid", "nyxid", model, gatewayEndpoint, string.Empty, defaultRoute);
    }

    private static Func<IAevatarSecretsStore> CreateSecretsStoreAccessor(
        AevatarAIFeatureOptions options,
        IServiceProvider services)
    {
        if (options.SecretsStore != null)
            return () => options.SecretsStore;

        // Prefer the DI-registered store so hosts that opted into the
        // read-only EnvironmentSecretsStore (e.g. mainnet) are honored
        // here too. Falling back to a fresh AevatarSecretsStore() would
        // re-open the local secrets.json on disk.
        var registered = services.GetService<IAevatarSecretsStore>();
        if (registered != null)
            return () => registered;

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

            var defaultRoute = IsNyxIdProviderType(semantic.ProviderType)
                ? ResolveNyxIdDefaultRoute(configuration)
                : null;
            configured.Add(new ConfiguredProvider(
                name.Trim(), semantic.ProviderType, resolvedModel, resolvedEndpoint, apiKey.Trim(), defaultRoute));
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
            ProviderKind.NyxId => new ProviderSemantic("nyxid", ResolveNyxIdDefaultModel(configuration, options), ResolveNyxIdGatewayEndpoint(configuration, options)),
            _ => new ProviderSemantic("openai", options.OpenAIModel, null),
        };
    }

    private static string? ResolveNyxIdGatewayEndpoint(IConfiguration configuration, AevatarAIFeatureOptions options)
    {
        if (options.NyxIdLlmEndpoint != null)
        {
            var authority = configuration["Cli:App:NyxId:Authority"]
                ?? configuration["Aevatar:NyxId:Authority"]
                ?? configuration["Aevatar:Authentication:Authority"];
            return NyxIdLlmEndpointResolver.ResolveEndpoint(authority, options.NyxIdLlmEndpoint);
        }

        return NyxIdLlmEndpointResolver.ResolveEndpoint(configuration);
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

    // The default route/model literals live once in LlmDefaults (Aevatar.AI.Abstractions) so the
    // NyxID server-default path and the OpenAI-compatible Responses ingress default share one
    // source and cannot drift. Every nyxid registration path resolves its default through these
    // helpers, which apply per-deployment config overrides on top.
    private static string ResolveNyxIdDefaultRoute(IConfiguration configuration) =>
        configuration["Aevatar:NyxId:DefaultRoute"] is { } route && !string.IsNullOrWhiteSpace(route)
            ? route.Trim()
            : LlmDefaults.NyxIdRoute;

    private static string ResolveNyxIdDefaultModel(IConfiguration configuration, AevatarAIFeatureOptions options) =>
        configuration["Aevatar:NyxId:DefaultModel"] is { Length: > 0 } model
            ? model
            : options.OpenAIModel;

    private sealed record ConfiguredProvider(
        string Name,
        string ProviderType,
        string Model,
        string? Endpoint,
        string ApiKey,
        string? DefaultRoutePreference = null);

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

    private static void RegisterServiceInvokeTools(IServiceCollection services, AevatarAIFeatureOptions options)
    {
        services.AddServiceInvokeTools(o =>
        {
            o.TenantId = options.ServiceInvokeTenantId;
            o.AppId = options.ServiceInvokeAppId;
            o.Namespace = options.ServiceInvokeNamespace;
            o.BypassInvokeApproval = options.BypassServiceInvokeApproval;
            o.EnableDynamicScopeResolution = true;
        });
    }

    private static void RegisterOrnnSkills(IServiceCollection services, AevatarAIFeatureOptions options)
    {
        // EnableOrnnSkills is the only gate. OrnnSkillClient routes through NyxID's proxy
        // (slug defaults to chrono-ornn's canonical "ornn") so the upstream Ornn URL is
        // not a configuration concern at this layer — NyxIdToolOptions.BaseUrl already
        // supplies the NyxID host, and NyxID resolves the Ornn backend from the catalog
        // entry matching the slug. Deployments override the slug only when their NyxID
        // catalog re-registered the service under a non-default name.
        services.AddOrnnSkills(o =>
        {
            if (!string.IsNullOrWhiteSpace(options.OrnnNyxIdSlug))
                o.NyxIdSlug = options.OrnnNyxIdSlug.Trim();
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOrnnSkillPublishAssetValidator, WorkflowOrnnSkillPublishAssetValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOrnnSkillPublishAssetValidator, ScriptOrnnSkillPublishAssetValidator>());
    }

    private static void RegisterSystemSkillOverlay(IServiceCollection services, AevatarAIFeatureOptions options)
    {
        if (!options.EnableSystemSkillOverlay || string.IsNullOrWhiteSpace(options.SystemSkillOverlaySetName))
            return;

        // Ensure the Ornn skill client (with the configured slug) is registered even when a host enables
        // the overlay without the Ornn skill tools; the overlay reads the set through this client.
        services.AddOrnnSkillClient(o =>
        {
            if (!string.IsNullOrWhiteSpace(options.OrnnNyxIdSlug))
                o.NyxIdSlug = options.OrnnNyxIdSlug.Trim();
        });

        services.AddSystemSkillOverlay(o =>
        {
            o.Enabled = options.EnableSystemSkillOverlay;
            o.SetName = options.SystemSkillOverlaySetName ?? string.Empty;
            o.RefreshTtl = options.SystemSkillOverlayRefreshTtl;
            o.MaxSkills = options.SystemSkillOverlayMaxSkills;
            o.MaxBytes = options.SystemSkillOverlayMaxBytes;
        });
    }

    private static void RegisterWebTools(IServiceCollection services, AevatarAIFeatureOptions options)
    {
        services.AddWebTools(o =>
        {
            o.NyxIdSearchSlug = options.WebSearchNyxIdSlug;
            o.SearchApiBaseUrl = options.WebSearchApiBaseUrl;
        });
    }

    private static void RegisterWorkflowTools(IServiceCollection services)
    {
        services.AddWorkflowTools();
    }

    private static void RegisterScriptingTools(IServiceCollection services)
    {
        services.AddScriptingTools();
    }

    private static void RegisterBindingTools(IServiceCollection services)
    {
        services.AddBindingTools();
    }

    private sealed class ServiceProviderAgentToolExecutionPort(IServiceProvider serviceProvider) : IAgentToolExecutionPort
    {
        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            serviceProvider.GetRequiredService<IAgentToolExecutionPort>().ExecuteAsync(request, ct);
    }
}
