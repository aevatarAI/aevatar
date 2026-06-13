using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
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
using Aevatar.AI.ToolProviders.Telegram;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.AI.ToolProviders.Web;
using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.Providers.NyxId;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Bootstrap.Hosting;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.ChatRouting;
using Aevatar.GAgents.ChatbotClassifier;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Telegram;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.StatusDashboard.DependencyInjection;
using Aevatar.GAgents.StatusDashboard.Executors;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Foundation.Runtime.Hosting.Maintenance;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Mainnet.Host.Api.ChatCompletions;
using Aevatar.Mainnet.Host.Api.ChatRouting;
using Aevatar.Mainnet.Host.Api.Messages;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Mainnet.Host.Api.Scheduled;
using Aevatar.Mainnet.Host.Api.Status;
using Aevatar.Mainnet.Host.Api.Voice;
using Aevatar.Studio.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Mainnet.Host.Api.Hosting;

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public static class MainnetHostBuilderExtensions
{
    internal const int ContainerHttpPort = 8080;
    internal const string ContainerListenUrl = "http://+:8080";
    internal const string LocalDevelopmentListenUrl = "http://127.0.0.1:5080";
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
        builder.AddMainnetDistributedOrleansHost();
        ConfigureMainnetListenUrls(builder);
        builder.AddAevatarPlatform(options =>
        {
            options.EnableMakerExtensions = true;
            options.ConfigureAIFeatures = ConfigureMainnetAIFeatures;
        });
        builder.AddGAgentServiceCapabilityBundle();
        builder.AddStudioCapability();

        // Authentication: config-driven, provider-agnostic
        builder.Services.AddNyxIdAuthentication();
        builder.AddAevatarAuthentication();
        builder.Services.AddNyxIdChat(builder.Configuration);
        builder.Services.AddStreamingProxy(builder.Configuration);
        builder.Services.AddChatbotClassifier();
        builder.Services.AddRetiredActorCleanup();
        builder.Services.AddChannelRuntime(builder.Configuration);
        builder.Services.AddChannelIdentity(builder.Configuration);
        builder.Services.Configure<AevatarOAuthClientEsAclOptions>(options =>
        {
            // Mainnet stores the cluster-singleton OAuth client readmodel in Elasticsearch.
            // Its read grant is intentionally scoped to the same internal services that can
            // read actor events, so the module-level fail-closed ES ACL guard may pass.
            options.GrantMatchesGrainEventStoreInternal = true;
            options.GrantDescription =
                "Mainnet aevatar-oauth-clients read grant matches grain/event-store internal services.";
        });
        builder.Services.AddDeviceRegistration(builder.Configuration);
        builder.Services.AddScheduledAgents(builder.Configuration);
        builder.Services.AddStatusDashboard(builder.Configuration);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IReadmodelFreshnessSource, ChannelBotRegistrationFreshnessSource>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHealthProbeExecutor, AevatarCoreLoopStatusProbeExecutor>());
        builder.Services.AddChatRoutingAgents(builder.Configuration);
        builder.Services.AddMainnetAgentProjectionDocumentStores(builder.Configuration);
        builder.Services.AddChatRoutingCore();
        builder.Services.Configure<ChatRoutingOptions>(options =>
        {
            options.Defaults.DefaultForwardToModelToolSetName = ToolSetNames.WorkspaceDefault;
        });
        // Implement (issue #695):
        //   Behavior: gate explicit /ws/voice/{actorId} bypass behind voice:bypass or admin.
        //   Why this shape: policy-aware /ws/voice owns normal routing; actor-id bypass stays dev/admin only.
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("voice-dev", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    PolicyAwareVoiceEndpoints.IsVoiceDevBypassPrincipal(context.User));
            });
        });
        builder.Services.TryAddSingleton<IResponsesCallerScopeResolver, NyxIdResponsesCallerScopeResolver>();
        builder.Services.Configure<ResponsesNyxIdIdentityAssertionOptions>(
            builder.Configuration.GetSection(ResponsesNyxIdIdentityAssertionOptions.SectionName));
        builder.Services.TryAddSingleton<NyxIdIdentityAssertionValidator>();
        builder.Services.TryAddSingleton<IResponsesChatRouteDecisionPort, ResponsesChatRouteDecisionPort>();
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
        // SkillRunner / WorkflowAgent / NyxidChat honor the bot owner's pre-configured LLM model
        // + route (issue #509). The bridge lives here, not in any agent or AI package, so
        // neither side has to depend on Studio.Application — the host is the natural composition
        // layer between Studio and the AI/agent packages that consume the port.
        builder.Services.TryAddSingleton<IOwnerLlmConfigSource, StudioUserConfigOwnerLlmConfigSource>();
        builder.Services.AddLarkAgentAuthoring();
        builder.Services.AddNyxIdRelayChannel();
        builder.Services.AddLarkPlatform();
        builder.Services.AddTelegramPlatform();
        builder.Services.AddChannelInteractiveReplyTools();
        builder.Services.AddChannelAdminTools();
        builder.Services.AddAgentCatalogTools();
        builder.Services.AddAevatarInvocationTools();
        builder.Services.Configure<DeviceEventOptions>(
            builder.Configuration.GetSection("Aevatar:DeviceEvents"));
        builder.Services.AddNyxIdTools(o =>
        {
            o.BaseUrl = builder.Configuration["Aevatar:NyxId:Authority"]
                        ?? builder.Configuration["Cli:App:NyxId:Authority"]
                        ?? builder.Configuration["Aevatar:Authentication:Authority"];
            // Opt-in: only the mainnet host (which runs the channel relay's approval-aware
            // tool execution pipeline) advertises ssh_exec to the LLM. Other hosts that pull
            // in NyxId tools (CLI, workflow runner) leave this off so a generic agent can't
            // shell into a remote without an approval gate. Defaults to false in
            // NyxIdToolOptions; flip via Aevatar:NyxId:EnableSshExecTool=true if a
            // deployment opts in.
            if (bool.TryParse(builder.Configuration["Aevatar:NyxId:EnableSshExecTool"], out var enableSsh))
                o.EnableSshExecTool = enableSsh;
            else
                o.EnableSshExecTool = true; // mainnet default: enabled (Lark bot needs it)
            o.BypassSshExecApproval = true; // mainnet Lark bot internal-only
        });
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
                    CreateToolSource<StartWorkflowToolSource>,
                    CreateToolSource<ObserveRunToolSource>,
                    CreateToolSource<ReadWorkflowRunArtifactToolSource>,
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
        });

        return builder;
    }

    private static IAgentToolSource CreateToolSource<TSource>(IServiceProvider serviceProvider)
        where TSource : class, IAgentToolSource
        => ActivatorUtilities.CreateInstance<TSource>(serviceProvider);

    public static WebApplication MapAevatarMainnetHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseAevatarDefaultHost();
        app.MapNyxIdChatEndpoints();
        app.MapChatRoutePolicyAdminEndpoints();
        app.MapVoicePresenceCapabilityAdminEndpoints();
        app.MapStreamingProxyEndpoints();
        app.MapResponsesApiEndpoints();
        app.MapMessagesApiEndpoints();
        app.MapChatCompletionsApiEndpoints();
        app.MapChannelCallbackEndpoints();
        app.MapDeviceEventEndpoints();
        app.MapIdentityOAuthEndpoints();
        app.MapSkillRunnerExternalTriggerEndpoints();
        app.MapStatusEndpoints();

        // Voice service registration is conditional on a configured provider
        // (RegisterVoicePresenceModules skips everything otherwise). Mapping
        // the real handlers without those services turns every /ws/voice
        // request into an unhandled DI 500 (issue #2023) — map the fail-closed
        // 503 stand-ins instead.
        if (PolicyAwareVoiceEndpoints.IsVoiceRealtimeConfigured(app.Services))
        {
            app.MapPolicyAwareVoiceEndpoint();
            app.MapVoicePresenceWebSocket("/ws/voice/{actorId}")
                .RequireAuthorization("voice-dev");
        }
        else
        {
            app.MapVoiceNotConfiguredEndpoints();
        }

        return app;
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
            PendingInjectionCapacity = options.PendingInjectionCapacity,
            TimeProvider = options.TimeProvider,
            DirectExternalEventTypeUrls = directEventTypeUrls,
            DirectExternalEventNoActiveSessionPolicy = options.DirectExternalEventNoActiveSessionPolicy,
        };
    }
}
