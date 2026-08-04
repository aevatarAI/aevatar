using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Application.CodexExecution;
using Aevatar.AI.Infrastructure.ChronoSandbox;
using Aevatar.AI.Infrastructure.ToolExecution;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.ToolProviders.AgentCatalog;
using Aevatar.AI.ToolProviders.AevatarInvocation;
using Aevatar.AI.ToolProviders.Channel;
using Aevatar.AI.ToolProviders.ChannelAdmin;
using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.AI.ToolProviders.StudioProvisioning;
using Aevatar.AI.ToolProviders.Telegram;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.AI.ToolProviders.Workflow;
using Aevatar.Authentication.Abstractions;
using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.Providers.NyxId;
using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.Audit.Hosting;
using Aevatar.BackendConsole.Hosting;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Bootstrap.Hosting;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgentService.Infrastructure.AgentProfiles;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.ChatRouting;
using Aevatar.GAgents.ChatbotClassifier;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Telegram;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.StatusDashboard.DependencyInjection;
using Aevatar.GAgents.StatusDashboard.Executors;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Foundation.Runtime.Hosting.Maintenance;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Mainnet.Host.Api.BackendConsole;
using Aevatar.Mainnet.Host.Api.Chat;
using Aevatar.Mainnet.Host.Api.ChatCompletions;
using Aevatar.Mainnet.Host.Api.ChatRouting;
using Aevatar.Mainnet.Host.Api.Cqrs;
using Aevatar.Mainnet.Host.Api.Messages;
using Aevatar.Mainnet.Host.Api.ManagedCodex;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using Aevatar.Mainnet.Host.Api.ProjectionRecovery;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Mainnet.Host.Api.Scheduled;
using Aevatar.Mainnet.Host.Api.Skills;
using Aevatar.Mainnet.Host.Api.Status;
using Aevatar.Mainnet.Host.Api.Voice;
using Aevatar.Mainnet.Host.Api.WorkflowAdmission;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Integration.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.Hosting;

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public static class MainnetHostBuilderExtensions
{
    internal const int ContainerHttpPort = 8080;
    internal const string ContainerListenUrl = "http://+:8080";
    internal const string LocalDevelopmentListenUrl = "http://127.0.0.1:5080";
    internal const string AgentToolAdmissionMaximumRequestLifetimeKey =
        "AgentToolAdmission:MaximumRequestLifetime";
    internal const string AgentToolAdmissionFutureClockSkewKey =
        "AgentToolAdmission:MaximumFutureClockSkew";
    internal const string AgentToolAdmissionKeyPrefixKey =
        "AgentToolAdmission:KeyPrefix";
    internal const string DefaultAgentToolAdmissionKeyPrefix =
        "aevatar:mainnet:agent-tool-admission:v1:";
    private const string NyxIdApiBaseUrlKey = "Aevatar:NyxId:ApiBaseUrl";
    private const string DeviceInboundDirectExternalEventTypeUrl =
        "type.googleapis.com/aevatar.gagents.household.DeviceInbound";

    public static WebApplicationBuilder AddAevatarMainnetHost(
        this WebApplicationBuilder builder,
        Action<AevatarDefaultHostOptions>? configureHost = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Host.UseDefaultServiceProvider(static (_, options) =>
        {
            // Mainnet must fail fast on container gaps instead of surfacing them
            // only when a hosted service or endpoint resolves the missing service.
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
        // Hosted services MUST start sequentially (the Generic Host default).
        //
        // 2026-06-03 prod incident: enabling HostOptions.ServicesStartConcurrently
        // raced the co-hosted Orleans silo reaching the Active lifecycle stage.
        // Grain-calling startup services (WorkflowDefinitionBootstrap,
        // ChannelBotRegistration, AevatarOAuthClientBootstrap, HealthProbeStartup,
        // StreamingProxyChatLifecycleContinuationRunner) fired their grain calls
        // before the silo could create activations, so every one failed with
        // "Unable to create local activation. Rejecting now." -> AggregateException
        // -> CrashLoopBackOff.
        //
        // Sequential startup runs hosted services in registration order: Kestrel
        // (binds the probe port early), then AddMainnetDistributedOrleansHost (silo
        // to Active), then the grain-calling services above — so grain activations
        // succeed. Liveness exposure is handled by binding http://+:8080 in the
        // container (see ConfigureMainnetListenUrls), not by parallelising startup.

        builder.AddAevatarDefaultHost(options =>
        {
            options.ServiceName = "Aevatar.Mainnet.Host.Api";
            options.EnableWebSockets = true;
            configureHost?.Invoke(options);
            // Mainnet invariant — enforced after the caller's configureHost so
            // user callbacks cannot re-enable the local file secrets store.
            // Secrets must come from AEVATAR_-prefixed environment variables;
            // Set/Remove on the secrets store will throw at the call site.
            options.AllowLocalFileSecretsStore = false;
        });
        builder.AddAevatarHostObservability("Aevatar.Mainnet.Host.Api");
        builder.AddMainnetDistributedOrleansHost();
        ConfigureMainnetListenUrls(builder);
        builder.AddAevatarPlatform(options =>
        {
            options.EnableMakerExtensions = true;
            // Mainnet invariant: the scripting capability (in-process Roslyn compile/execute of
            // tenant-supplied C#) must never be composed into this host. Stated explicitly so a
            // future change to the platform default cannot silently re-enable it here.
            options.EnableScriptingCapability = false;
            options.MapWorkflowChatPost = false;
            options.ConfigureAIFeatures = ConfigureMainnetAIFeatures;
        });
        var agentToolAdmissionPolicy = ResolveAgentToolAdmissionPolicy(builder.Configuration);
        if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.AddInMemoryAgentToolAdmissionLedger(agentToolAdmissionPolicy);
        }
        else
        {
            builder.Services.AddGarnetAgentToolAdmissionLedger(
                ResolveAgentToolAdmissionLedgerOptions(builder.Configuration),
                agentToolAdmissionPolicy);
        }
        // Hosted services start in registration order. Register the provider-local index
        // reconcile before capability modules can add startup readers so schema drift is
        // migrated before any read-model query executes.
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ElasticsearchProjectionIndexReconcileHostedService>());
        builder.AddGAgentServiceCapabilityBundle();
        builder.Services.AddAgentProfileApplication();
        builder.Services.TryAddSingleton<IAgentProfileActorPort, AgentProfileActorPort>();
        builder.Services.TryAddSingleton<AgentProfileApplicationService>();
        builder.AddStudioCapability();
        builder.Services.AddAuditTrailCore(builder.Configuration);
        builder.AddAuditTrailCapabilityBundle();
        builder.Services.AddBackendConsoleStaticAssets(builder.Configuration);

        // 06-26 ornn skills invocation page: host-side catalog read surface (composes the Ornn skill client).
        builder.Services.AddSingleton<IUserSkillCatalogQueryService, UserSkillCatalogQueryService>();
        builder.Services.AddSingleton<IUserSkillRunService, UserSkillRunService>();

        // Authentication: config-driven, provider-agnostic
        ConfigureMainnetAuthenticationAudience(builder);
        builder.Services.AddNyxIdAuthentication();
        builder.AddAevatarAuthentication();
        builder.AddNyxIdIdentityAssertionAuthentication();
        if (builder.Configuration[$"{NyxIdAssistantActionsOptions.ConfigSection}:Enabled"] is null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{NyxIdAssistantActionsOptions.ConfigSection}:Enabled"] = bool.TrueString,
            });
        }
        builder.Services.AddNyxIdChat(builder.Configuration);
        AddNyxIdChatAgentProfile(builder);
        builder.Services.AddStreamingProxy(builder.Configuration);
        builder.Services.AddChatbotClassifier();
        builder.Services.AddRetiredActorCleanup();
        builder.Services.AddChannelRuntime(builder.Configuration);
        builder.Services.AddChannelIdentity(builder.Configuration);
        var configuredSandboxServiceSlug = builder.Configuration["Aevatar:NyxId:SandboxServiceSlug"];
        var sandboxServiceSlug = string.IsNullOrWhiteSpace(configuredSandboxServiceSlug)
            ? NyxIdToolOptions.DefaultSandboxServiceSlug
            : configuredSandboxServiceSlug.Trim();
        builder.Services.Configure<NyxIdBrokerOptions>(options =>
        {
            var configuredRoute = builder.Configuration["Aevatar:NyxId:DefaultRoute"];
            options.RequiredLlmServiceSlug = string.IsNullOrWhiteSpace(configuredRoute)
                ? LlmDefaults.NyxIdRoute
                : configuredRoute.Trim();
            var configuredOrnnSlug = builder.Configuration["Aevatar:Ornn:NyxIdSlug"];
            var ornnSlug = string.IsNullOrWhiteSpace(configuredOrnnSlug)
                ? OrnnOptions.DefaultNyxIdSlug
                : configuredOrnnSlug.Trim();
            options.AdditionalRequiredServiceSlugs = builder.Configuration
                .GetSection("Aevatar:NyxId:AdditionalRequiredServiceSlugs")
                .GetChildren()
                .Select(static child => child.Value)
                .Where(static serviceSlug => !string.IsNullOrWhiteSpace(serviceSlug))
                .Select(static serviceSlug => serviceSlug!.Trim())
                .Append(ornnSlug)
                .Append(sandboxServiceSlug)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        });
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<NyxIdBrokerOptions>, MainnetNyxIdResourcePolicyValidator>());
        builder.Services.AddDeviceRegistration(builder.Configuration);
        builder.Services.AddScheduledAgents(builder.Configuration);
        builder.Services.AddStatusDashboard(builder.Configuration);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IReadmodelFreshnessSource, ChannelBotRegistrationFreshnessSource>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthProbeExecutor, AevatarCoreLoopStatusProbeExecutor>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthProbeExecutor, AuditQueryIndexStatusProbeExecutor>());
        // Self-issued scope service token source for credentialed orchestration/observatory probes.
        // IScopeServiceTokenIssuer is only registered when scope service tokens are enabled, so it is
        // resolved optionally — absent it the provider returns null and those probes read "unknown".
        builder.Services.AddSingleton<IProbeServiceTokenProvider>(
            sp => new ScopeServiceProbeTokenProvider(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<IScopeServiceTokenIssuer>()));
        builder.Services.AddChatRoutingAgents(builder.Configuration);
        builder.Services.AddMainnetAgentProjectionDocumentStores(builder.Configuration);
        builder.Services.AddChatRoutingCore();
        builder.Services.Configure<ChatRoutingOptions>(options =>
        {
            options.Defaults.DefaultForwardToModelToolSetName = ToolSetNames.WorkspaceDefault;
        });
        builder.Services.TryAddSingleton<IResponsesCallerScopeResolver, NyxIdResponsesCallerScopeResolver>();
        builder.Services.Configure<ResponsesNyxIdIdentityAssertionOptions>(
            builder.Configuration.GetSection(ResponsesNyxIdIdentityAssertionOptions.SectionName));
        // Mainnet's single-use assertion guarantee must survive load balancing across replicas.
        // Development keeps the deterministic in-memory implementation; every other environment
        // requires the shared Garnet connection composed by the distributed runtime.
        if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.TryAddSingleton<IIdentityAssertionReplayGuard>(
                sp => new InMemoryIdentityAssertionReplayGuard(sp.GetRequiredService<TimeProvider>()));
        }
        else
        {
            builder.Services.TryAddSingleton<IIdentityAssertionSingleUseStore, GarnetIdentityAssertionSingleUseStore>();
            builder.Services.TryAddSingleton<IIdentityAssertionReplayGuard, DistributedIdentityAssertionReplayGuard>();
        }
        builder.Services.TryAddSingleton<NyxIdIdentityAssertionValidator>();
        builder.Services.TryAddSingleton<IResponsesChatRouteDecisionPort, ResponsesChatRouteDecisionPort>();
        // Default model for direct OpenAI-compatible ingress (/v1/responses, /v1/messages,
        // /v1/chat/completions) when the caller omits `model`. The slug/model value is a
        // host fact supplied by configuration; an explicit caller model always wins.
        builder.Services.Configure<ResponsesIngressOptions>(
            builder.Configuration.GetSection(ResponsesIngressOptions.SectionName));
        builder.Services.TryAddSingleton<IResponsesCommandFacade, ResponsesCommandFacade>();
        builder.Services.TryAddSingleton<IMessagesCommandFacade, MessagesCommandFacade>();
        builder.Services.TryAddSingleton<IChatCompletionsCommandFacade, ChatCompletionsCommandFacade>();
        builder.Services.TryAddSingleton<ILlmSessionRunObservationService, LlmSessionRunObservationService>();
        builder.Services.TryAddSingleton<IResponsesWebSubstituteBackend, ResponsesWebSubstituteBackendAdapter>();
        builder.Services.TryAddSingleton<ResponsesWebSubstituteToolExecutionService>();
        builder.Services.TryAddSingleton<IResponsesToolClassificationService, ResponsesToolClassificationService>();
        builder.Services.TryAddSingleton<IResponsesDirectToolPlanService, ResponsesDirectToolPlanService>();
        builder.Services.TryAddSingleton<IResponsesModelsAggregator, NyxIdResponsesModelsAggregator>();
        // Refactor (iter26/cluster-026-responses-route-user-catalog-cache):
        //   Old pattern: Responses/Messages routes resolve `vendor/model` by reading a singleton per-bearer in-process cache of NyxID user LLM service catalog facts.
        //   New principle: Resolve model route from the current catalog read in the request flow; do not store user route facts in singleton process memory.
        builder.Services.TryAddSingleton<IResponsesRouteResolver, ResponsesRouteResolver>();
        builder.Services.Configure<ResponsesModelMetadataFallbackOptions>(options =>
        {
            // Bind a flat slug-or-slug/model → fallback dictionary from
            // `Aevatar:Responses:ModelMetadataFallbacks` directly so deployments can
            // express it with the natural shape `{slug: {context_length, ...}}` instead
            // of the wrapped `{Entries: {…}}` shape that automatic-binding would force.
            var section = builder.Configuration.GetSection(ResponsesModelMetadataFallbackOptions.SectionName);
            foreach (var entry in section.GetChildren())
            {
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;
                var fallback = entry.Get<ResponsesModelMetadataFallback>();
                if (fallback is null) continue;
                options.Entries[entry.Key] = fallback;
            }
        });
        builder.Services.AddHttpClient();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponsesToolProvider, ResponsesAevatarToolProvider>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponsesToolProvider, ResponsesUserSkillsToolProvider>());
        // Bridge Studio's IUserConfigQueryPort onto the AI-layer IOwnerLlmConfigSource port so
        // scheduled workflow dispatch, workflow agents, and NyxidChat honor the bot owner's
        // pre-configured LLM model + route (issue #509). The bridge lives here, not in any agent or AI package, so
        // neither side has to depend on Studio.Application — the host is the natural composition
        // layer between Studio and the AI/agent packages that consume the port.
        builder.Services.TryAddSingleton<IOwnerLlmConfigSource, StudioUserConfigOwnerLlmConfigSource>();
        builder.Services.AddSkillBackedHumanInteractionDelivery();
        builder.Services.AddChannelBackedHumanInteractionTools();
        builder.Services.AddNyxIdRelayChannel();
        builder.Services.AddLarkPlatform();
        builder.Services.AddTelegramPlatform();
        builder.Services.AddChannelInteractiveReplyTools();
        builder.Services.AddChannelAdminTools();
        builder.Services.AddAgentCatalogTools();
        builder.Services.AddAevatarInvocationTools();
        // Studio workflow scheduling tool (aevatar_provision_workflow_schedule): the channel-free,
        // Observatory-delivered analogue of the Lark scheduled_agent_creator. Registered as an
        // IAgentToolSource here; the studio workflow's allowed_tools allowlist (W2) scopes it to
        // studio runs. The narrow IWorkflowScheduleProvisioningPort it depends on is registered by
        // AddStudioApplication (via AddStudioCapability), composed in the same host container.
        builder.Services.AddStudioProvisioningTools();
        builder.Services.Configure<DeviceEventOptions>(
            builder.Configuration.GetSection("Aevatar:DeviceEvents"));
        // Fail-fast: device HMAC verification must never be disabled in production.
        var deviceEventOptions = builder.Configuration.GetSection("Aevatar:DeviceEvents").Get<DeviceEventOptions>()
            ?? new DeviceEventOptions();
        deviceEventOptions.EnsureNotSkippingHmacInProduction(builder.Environment.IsProduction());
        // NyxID-backed current-user resolver plus aevatar admin access policy.
        builder.Services.AddNyxIdPlatformAuthorization(builder.Configuration);
        builder.Services.AddChronoSandboxCodexExecution(
            builder.Configuration,
            builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"));
        builder.Services.AddNyxIdTools(o =>
        {
            // Override the single default (NyxIdToolOptions.DefaultBaseUrl) only when config provides a
            // non-empty value; an absent/empty config key must NOT clobber the default to null.
            var nyxAuthority = builder.Configuration["Aevatar:NyxId:ApiBaseUrl"]
                               ?? builder.Configuration["Aevatar:NyxId:Authority"]
                               ?? builder.Configuration["Cli:App:NyxId:Authority"]
                               ?? builder.Configuration["Aevatar:Authentication:Authority"];
            if (!string.IsNullOrWhiteSpace(nyxAuthority))
                o.BaseUrl = nyxAuthority;
            o.SandboxServiceSlug = sandboxServiceSlug;
            // SSH-backed tools are disabled unless the deployment opts in explicitly.
            // Even when exposed, their contract always requires a durable actor-owned grant.
            if (bool.TryParse(builder.Configuration["Aevatar:NyxId:EnableSshExecTool"], out var enableSsh))
                o.EnableSshExecTool = enableSsh;
            o.EnableManagedCodexExecTool = builder.Configuration.GetValue<bool>(
                $"{ManagedCodexOptions.SectionName}:Enabled");
            o.MaxRequestDurationSeconds = builder.Configuration.GetValue(
                "Aevatar:NyxId:MaxRequestDurationSeconds",
                o.MaxRequestDurationSeconds);
            if (long.TryParse(builder.Configuration["Aevatar:NyxId:ProxyFileArtifactMaxBytes"], out var maxBytes))
                o.ProxyFileArtifactMaxBytes = maxBytes;
            o.ManagedWorkflowAdmissionMode = builder.Configuration.GetValue(
                "Aevatar:NyxId:ManagedWorkflowAdmissionMode",
                o.ManagedWorkflowAdmissionMode);
        });
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, NyxIdWorkflowAdmissionEnforcementStartupGuard>());
        builder.Services.Replace(ServiceDescriptor.Singleton<
            IWorkflowFileMultipartUploadPolicyResolver,
            MainnetWorkflowFileMultipartUploadSafetyPolicyResolver>());
        builder.Services.AddLarkTools(o =>
        {
            o.ProviderSlug = builder.Configuration["Aevatar:Lark:NyxProviderSlug"] ?? "api-lark-bot";
            if (bool.TryParse(builder.Configuration["Aevatar:Lark:EnableWorkflowFileSubmit"], out var enableWorkflowFileSubmit))
                o.EnableWorkflowFileSubmit = enableWorkflowFileSubmit;
        });
        builder.Services.AddTelegramTools(o =>
        {
            o.ProviderSlug = builder.Configuration["Aevatar:Telegram:NyxProviderSlug"] ?? "api-telegram-bot";
        });
        builder.Services.AddChronoStorageTools(o =>
        {
            // Self-referencing: the explorer endpoints are served by this same host.
            var urls = builder.Configuration[WebHostDefaults.ServerUrlsKey] ?? "http://127.0.0.1:5080";
            o.ApiBaseUrl = urls.Split(';').FirstOrDefault()?.Trim();
        });
        builder.Services.AddWebTools(o =>
        {
            o.NyxIdBaseUrl = builder.Configuration["Aevatar:NyxId:Authority"]
                             ?? builder.Configuration["Cli:App:NyxId:Authority"]
                             ?? builder.Configuration["Aevatar:Authentication:Authority"];
            o.NyxIdSearchSlug = builder.Configuration["Aevatar:Web:NyxIdSearchSlug"]
                                ?? builder.Configuration["Aevatar:Web:SearchSlug"];
            o.SearchApiBaseUrl = builder.Configuration["Aevatar:Web:SearchApiBaseUrl"];
        });
        builder.Services.AddToolSetRegistry(options =>
        {
            options.AddToolSet(
                ToolSetNames.WorkspaceDefault,
                [
                    CreateToolSource<InvokeGAgentToolSource>,
                    CreateToolSource<InvokeTeamToolSource>,
                    CreateToolSource<InvokeMemberToolSource>,
                    CreateToolSource<StartWorkflowToolSource>,
                    CreateToolSource<ObserveRunToolSource>,
                    CreateToolSource<ReadWorkflowRunArtifactToolSource>,
                    CreateToolSource<WorkflowCatalogAgentToolSource>,
                    CreateToolSource<ProvisionWorkflowScheduleToolSource>,
                    CreateToolSource<CreateStudioTeamToolSource>,
                    CreateToolSource<StudioTeamQueryToolSource>,
                    CreateToolSource<CreateStudioMemberToolSource>,
                    CreateToolSource<CreateStudioMemberWorkflowDraftToolSource>,
                    CreateToolSource<StudioMemberQueryToolSource>,
                    CreateToolSource<StudioScheduleQueryToolSource>,
                    CreateToolSource<StudioWorkflowQueryToolSource>,
                    CreateToolSource<BindStudioMemberWorkflowToolSource>,
                    CreateToolSource<ScheduleStudioMemberWorkflowToolSource>,
                    CreateToolSource<ResponsesAevatarToolProvider>,
                    CreateToolSource<ChannelInteractiveReplyToolSource>,
                    CreateToolSource<ChannelRegistrationToolSource>,
                    CreateToolSource<AgentDeliveryTargetToolSource>,
                    CreateToolSource<NyxIdAgentToolSource>,
                    CreateToolSource<LarkAgentToolSource>,
                    CreateToolSource<TelegramAgentToolSource>,
                    CreateToolSource<ChronoStorageAgentToolSource>,
                    CreateToolSource<WebAgentToolSource>,
                    CreateToolSource<SkillsAgentToolSource>,
                    CreateToolSource<OrnnAgentToolSource>,
                ],
                "Default /v1/responses workspace tool composition.");
            options.AddToolSet(
                ToolSetNames.LarkSelfNotify,
                [ToolSetNames.WorkspaceDefault],
                [],
                "Lark route tool composition with the default workspace tools.");
            // Opt-in only: connected-service tools carry per-user NyxID surfaces, so this set
            // is referenced by route policy (not folded into workspace.default) to avoid
            // injecting every caller's connected services by default.
            options.AddToolSet(
                ToolSetNames.NyxIdConnectedServices,
                [CreateToolSource<NyxIdConnectedServiceToolSource>],
                "NyxID connected-service operations explicitly marked x-aevatar-tool, registered as individual tools.");
            options.AddToolSet(
                AgentProfilePolicies.NyxIdChatRouteToolSet,
                [ToolSetNames.WorkspaceDefault, ToolSetNames.NyxIdConnectedServices],
                [],
                "NyxID chat profile route tool composition with workspace and typed connected-service tools.");
        });

        return builder;
    }

    private static void AddNyxIdChatAgentProfile(WebApplicationBuilder builder)
    {
        builder.Services.Replace(
            ServiceDescriptor.Singleton<INyxIdChatAgentProfileResolver,
                MainnetNyxIdChatAgentProfileResolver>());
    }

    private static IAgentToolSource CreateToolSource<TSource>(IServiceProvider serviceProvider)
        where TSource : class, IAgentToolSource
        => ActivatorUtilities.CreateInstance<TSource>(serviceProvider);

    public static WebApplication MapAevatarMainnetHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseAevatarDefaultHost();
        app.MapMainnetChatEndpoints();
        app.MapNyxIdChatPublicEndpoints();
        app.MapNyxIdChatEndpoints();
        app.MapChatRoutePolicyAdminEndpoints();
        app.MapAgentProfileEndpoints();
        app.MapVoicePresenceCapabilityAdminEndpoints();
        app.MapVoiceConsoleEndpoints();
        app.MapAutoConsoleCallbackEndpoints();
        app.MapAdminConsoleEndpoints();
        app.MapCqrsObservatoryPageEndpoints();
        app.MapCqrsObservatoryApiEndpoints();
        app.MapStreamingProxyEndpoints();
        app.MapResponsesApiEndpoints();
        app.MapMessagesApiEndpoints();
        app.MapChatCompletionsApiEndpoints();
        app.MapChannelCallbackEndpoints();
        app.MapChannels();
        app.MapDeviceEventEndpoints();
        app.MapIdentityOAuthEndpoints();
        app.MapScheduledAgentCredentialRepairAdminEndpoints();
        app.MapDevelopmentNyxIdApiKeyEndpoints();
        app.MapProjectionVersionRegressionRepairAdminEndpoints();
        app.MapManagedCodexCredentialEndpoints();
        app.MapWorkflowSkillsEndpoints();
        app.MapStatusEndpoints();

        // Voice service registration is conditional on a configured provider
        // (RegisterVoicePresenceModules skips everything otherwise). Mapping
        // the real handlers without those services turns every /ws/voice
        // request into an unhandled DI 500 (issue #2023) — map the fail-closed
        // 503 stand-ins instead.
        if (PolicyAwareVoiceEndpoints.IsVoiceRealtimeConfigured(app.Services))
        {
            app.MapPolicyAwareVoiceEndpoint();
            app.MapPolicyAwareVoiceWhipEndpoint();
        }
        else
        {
            app.MapVoiceNotConfiguredEndpoints();
        }

        return app;
    }

    private static void ConfigureMainnetAuthenticationAudience(WebApplicationBuilder builder)
    {
        var audienceKey = $"{AevatarAuthenticationOptions.SectionName}:Audience";
        if (!string.IsNullOrWhiteSpace(builder.Configuration[audienceKey]))
            return;

        // NyxID access tokens use its API BASE_URL as their audience. Identity assertions use
        // a separate audience and must not be reused for bearer-token validation.
        var nyxIdApiBaseUrl = builder.Configuration[NyxIdApiBaseUrlKey];
        if (string.IsNullOrWhiteSpace(nyxIdApiBaseUrl))
            return;

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [audienceKey] = nyxIdApiBaseUrl.Trim(),
        });
    }

    private static void ConfigureMainnetListenUrls(WebApplicationBuilder builder)
    {
        var configuredUrls = builder.Configuration[WebHostDefaults.ServerUrlsKey];
        var resolvedUrls = ResolveMainnetListenUrls(
            configuredUrls,
            IsRunningInContainer());
        if (!string.Equals(configuredUrls, resolvedUrls, StringComparison.Ordinal))
            builder.WebHost.UseUrls(resolvedUrls);
    }

    private static AgentToolAdmissionPolicy ResolveAgentToolAdmissionPolicy(
        IConfiguration configuration)
    {
        var defaults = AgentToolAdmissionPolicy.Default;
        return new AgentToolAdmissionPolicy(
            configuration.GetValue<TimeSpan?>(AgentToolAdmissionMaximumRequestLifetimeKey) ??
            AgentToolAdmissionPolicy.DefaultMaximumRequestLifetime,
            configuration.GetValue<TimeSpan?>(AgentToolAdmissionFutureClockSkewKey) ??
            defaults.MaximumFutureClockSkew);
    }

    private static AgentToolAdmissionLedgerOptions ResolveAgentToolAdmissionLedgerOptions(
        IConfiguration configuration) =>
        new(configuration[AgentToolAdmissionKeyPrefixKey]?.Trim() ?? DefaultAgentToolAdmissionKeyPrefix);

    internal static string ResolveMainnetListenUrls(string? configuredUrls, bool runningInContainer)
    {
        if (runningInContainer)
        {
            if (string.IsNullOrWhiteSpace(configuredUrls))
                return ContainerListenUrl;

            var trimmed = configuredUrls.Trim();
            return ListenUrlsIncludePort(trimmed, ContainerHttpPort)
                ? trimmed
                : $"{trimmed};{ContainerListenUrl}";
        }

        return string.IsNullOrWhiteSpace(configuredUrls)
            ? LocalDevelopmentListenUrl
            : configuredUrls.Trim();
    }

    private static bool ListenUrlsIncludePort(string listenUrls, int port) =>
        listenUrls
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => ListenUrlIncludesPort(candidate, port));

    private static bool ListenUrlIncludesPort(string candidate, int port)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            uri.Port == port)
        {
            return true;
        }

        return candidate.EndsWith($":{port}", StringComparison.Ordinal) ||
               candidate.Contains($":{port}/", StringComparison.Ordinal);
    }

    private static bool IsRunningInContainer()
    {
        var value = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "1", StringComparison.Ordinal);
    }

    private static void ConfigureMainnetAIFeatures(AevatarAIFeatureOptions options)
    {
        options.EnableBindingTools = true;

        if (!options.VoicePresence.Module.DirectExternalEventTypeUrls.Contains(
                DeviceInboundDirectExternalEventTypeUrl,
                StringComparer.Ordinal))
        {
            options.VoicePresence.Module = CloneVoicePresenceModuleOptionsWithDirectEventType(
                options.VoicePresence.Module,
                DeviceInboundDirectExternalEventTypeUrl);
        }
    }

    private static VoicePresenceModuleOptions CloneVoicePresenceModuleOptionsWithDirectEventType(
        VoicePresenceModuleOptions options,
        string typeUrl)
    {
        var directEventTypeUrls = options.DirectExternalEventTypeUrls
            .Append(typeUrl)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new VoicePresenceModuleOptions
        {
            Name = options.Name,
            Priority = options.Priority,
            LinkId = options.LinkId,
            StaleAfter = options.StaleAfter,
            DedupeWindow = options.DedupeWindow,
            ToolExecutionTimeout = options.ToolExecutionTimeout,
            DrainTimeout = options.DrainTimeout,
            PendingInjectionCapacity = options.PendingInjectionCapacity,
            TimeProvider = options.TimeProvider,
            DirectExternalEventTypeUrls = directEventTypeUrls,
            DirectExternalEventNoActiveSessionPolicy = options.DirectExternalEventNoActiveSessionPolicy,
        };
    }
}
