using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Application.CodexExecution;
using Aevatar.AI.Infrastructure.ChronoSandbox;
using Aevatar.AI.Infrastructure.ToolExecution;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.ToolProviders.AgentCatalog;
using Aevatar.AI.ToolProviders.AevatarInvocation;
using Aevatar.AI.ToolProviders.Binding;
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
using Aevatar.Bootstrap.Connectors;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Bootstrap.Hosting;
using Aevatar.ChatRouting.Core;
using Aevatar.Configuration;
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
using Aevatar.Mainnet.Host.Api.AI;
using Aevatar.Mainnet.Host.Api.Chat;
using Aevatar.Mainnet.Host.Api.ChatCompletions;
using Aevatar.Mainnet.Host.Api.ChatRouting;
using Aevatar.Mainnet.Host.Api.Cqrs;
using Aevatar.Mainnet.Host.Api.Messages;
using Aevatar.Mainnet.Host.Api.ManagedCodex;
using Aevatar.Mainnet.Host.Api.ModelCatalog;
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
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Integration.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Google.Protobuf.WellKnownTypes;

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
        // Grain-calling startup services (ChannelBotRegistration,
        // AevatarOAuthClientBootstrap, HealthProbeStartup,
        // StreamingProxyChatLifecycleContinuationRunner) fired their grain calls
        // before the silo could create activations, so every one failed with
        // "Unable to create local activation. Rejecting now." -> AggregateException
        // -> CrashLoopBackOff.
        //
        // Sequential startup runs hosted services in registration order: Kestrel
        // (binds the probe port early), then AddMainnetDistributedOrleansHost (silo
        // to Active), then the grain-calling services above — so grain activations
        // succeed. WorkflowDefinitionBootstrap performs file loading in StartAsync
        // and actor materialization in StartedAsync so a slow committed observation
        // cannot block the probe port. Liveness exposure is handled by binding
        // http://+:8080 in the container (see ConfigureMainnetListenUrls), not by
        // parallelising startup.

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
        builder.Services.PostConfigure<WorkflowDefinitionFileSourceOptions>(options =>
        {
            if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
            {
                options.SkipSourceCredentialRequiredDefinitionsOnStartup = true;
            }
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
        builder.Services.TryAddSingleton<IAgentProfileCatalogApplicationService>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentProfileApplicationService>());
        builder.Services.AddAIWorkspace(builder.Configuration);
        builder.AddStudioCapability();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostConnectorCatalogDefaults,
            MainnetDeterministicComputeConnectorCatalogDefaults>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            MainnetDeterministicComputeConnectorHostedService>());
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
        builder.Services.Replace(ServiceDescriptor.Singleton(
            NyxIdChatCanaryEffectFaultOptions.EnabledFor(
                "5d0d7b72-acff-49af-bb1b-9f30bbb7c102")));
        AddNyxIdChatAgentProfile(builder);
        builder.Services.AddStreamingProxy(builder.Configuration);
        builder.Services.AddChatbotClassifier();
        builder.Services.AddRetiredActorCleanup();
        builder.Services.AddChannelRuntime(builder.Configuration);
        builder.Services.AddChannelIdentity(builder.Configuration);
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
                .Append(CodeExecutionContract.ServiceSlug)
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
        // Refactor (iter26/cluster-026-responses-route-user-catalog-cache):
        //   Old pattern: Responses/Messages routes resolve `vendor/model` by reading a singleton per-bearer in-process cache of NyxID user LLM service catalog facts.
        //   New principle: Resolve model route from the current catalog read in the request flow; do not store user route facts in singleton process memory.
        builder.Services.TryAddSingleton<IResponsesRouteResolver, ResponsesRouteResolver>();
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
        builder.Services.AddNyxIdTools(builder.Configuration, o =>
        {
            // SSH-backed tools are disabled unless the deployment opts in explicitly.
            // Even when exposed, their contract always requires a durable actor-owned grant.
            if (bool.TryParse(builder.Configuration["Aevatar:NyxId:EnableSshExecTool"], out var enableSsh))
                o.EnableSshExecTool = enableSsh;
            // Milestone 40 ships actor-owned connected-service effects on Mainnet. Other hosts
            // retain NyxIdToolOptions' fail-closed default until they provide the same durable facts.
            o.EnableAssistantConnectedServiceEffects = true;
            o.AssistantOperationReadBackBindings.Add(new NyxIdAssistantOperationReadBackBinding
            {
                CatalogServiceSlug = "api-lark-bot",
                EffectHttpMethod = "POST",
                EffectPathTemplate = "/open-apis/im/v1/messages",
                ReadHttpMethod = "GET",
                ReadPathTemplate = "/open-apis/im/v1/messages/{message_id}",
                CheckName = "lark_provider_message_visible_by_id",
                Match = AgentToolReadBackMatch.ArrayContainsEquals,
                JsonPointer = "/data/items",
                ElementJsonPointer = "/message_id",
                EffectResultIdentityJsonPointer = "/data/message_id",
                ProviderResourceArgument = new NyxIdAssistantReadBackProviderResourceArgument
                {
                    ReadLocation = NyxIdAssistantOperationArgumentLocation.Path,
                    ReadArgumentName = "message_id",
                },
            });
            o.AssistantOperationReadBackBindings.Add(new NyxIdAssistantOperationReadBackBinding
            {
                CatalogServiceSlug = "api-lark-bot",
                EffectHttpMethod = "POST",
                EffectPathTemplate = "/open-apis/approval/v4/instances",
                ReadHttpMethod = "GET",
                ReadPathTemplate = "/open-apis/approval/v4/instances/{instance_id}",
                CheckName = "lark_approval_instance_exists_by_caller_uuid",
                Match = AgentToolReadBackMatch.Exists,
                JsonPointer = "/data/instance_code",
                EffectResultIdentityJsonPointer = "/data/instance_code",
                ArgumentBindings =
                [
                    new NyxIdAssistantReadBackArgumentBinding
                    {
                        EffectLocation = NyxIdAssistantOperationArgumentLocation.Body,
                        EffectArgumentName = "uuid",
                        ReadLocation = NyxIdAssistantOperationArgumentLocation.Path,
                        ReadArgumentName = "instance_id",
                    },
                ],
                NotAppliedEvidence = new NyxIdAssistantReadBackNotAppliedEvidence
                {
                    JsonPointer = "/code",
                    ExpectedValue = Value.ForNumber(1390003),
                },
            });
            o.AssistantOperationReadBackBindings.Add(new NyxIdAssistantOperationReadBackBinding
            {
                CatalogServiceSlug = "api-lark-bot",
                EffectHttpMethod = "POST",
                EffectPathTemplate =
                    "/open-apis/bitable/v1/apps/{app_token}/tables/{table_id}/records",
                ReadHttpMethod = "GET",
                ReadPathTemplate =
                    "/open-apis/bitable/v1/apps/{app_token}/tables/{table_id}/records",
                CheckName = "lark_bitable_record_exists_by_provider_identity",
                Match = AgentToolReadBackMatch.ArrayContainsEquals,
                JsonPointer = "/data/items",
                ElementJsonPointer = "/record_id",
                EffectResultIdentityJsonPointer = "/data/record/record_id",
                ArgumentBindings =
                [
                    new NyxIdAssistantReadBackArgumentBinding
                    {
                        EffectLocation = NyxIdAssistantOperationArgumentLocation.Path,
                        EffectArgumentName = "app_token",
                        ReadLocation = NyxIdAssistantOperationArgumentLocation.Path,
                        ReadArgumentName = "app_token",
                    },
                    new NyxIdAssistantReadBackArgumentBinding
                    {
                        EffectLocation = NyxIdAssistantOperationArgumentLocation.Path,
                        EffectArgumentName = "table_id",
                        ReadLocation = NyxIdAssistantOperationArgumentLocation.Path,
                        ReadArgumentName = "table_id",
                    },
                ],
                LiteralReadArguments =
                [
                    new NyxIdAssistantReadBackLiteralArgument
                    {
                        ReadLocation = NyxIdAssistantOperationArgumentLocation.Query,
                        ReadArgumentName = "page_size",
                        Value = Value.ForNumber(20),
                    },
                ],
                Pagination = new NyxIdAssistantReadBackPagination
                {
                    HasMoreJsonPointer = "/data/has_more",
                    PageTokenJsonPointer = "/data/page_token",
                    PageTokenLocation = NyxIdAssistantOperationArgumentLocation.Query,
                    PageTokenArgumentName = "page_token",
                    MaxPages = 200,
                },
            });
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
        ReplaceMainnetWorkflowAgentToolSourceAdapter(builder.Services);
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
        // AddAevatarPlatform registers WebToolOptions before Mainnet applies its host
        // invariants. Replace that instance explicitly so a mounted appsettings.json
        // cannot keep an obsolete provider slug alive through the earlier registration.
        builder.Services.Replace(ServiceDescriptor.Singleton(new WebToolOptions
        {
            NyxIdBaseUrl = FirstConfiguredValue(
                    builder.Configuration,
                    "Aevatar:Web:NyxIdBaseUrl")
                ?? NyxIdEndpointResolver.ResolvePublicApiBaseUrl(builder.Configuration),
            NyxIdSearchSlug = FirstConfiguredValue(
                    builder.Configuration,
                    "Aevatar:Web:NyxIdSearchSlug",
                    "Aevatar:Web:SearchSlug",
                    "Aevatar:WebSearch:NyxIdSlug")
                ?? "tavily-search",
            NyxIdSearchProvider = FirstConfiguredValue(
                builder.Configuration,
                "Aevatar:Web:NyxIdSearchProvider",
                "Aevatar:Web:SearchProvider",
                "Aevatar:WebSearch:Provider"),
            SearchApiBaseUrl = FirstConfiguredValue(
                builder.Configuration,
                "Aevatar:Web:SearchApiBaseUrl",
                "Aevatar:WebSearch:ApiBaseUrl"),
        }));
        builder.Services.AddToolSetRegistry(options =>
        {
            options.AddToolSet(
                ToolSetNames.ChatCore,
                [CreateToolSource<AskUserAgentToolSource>],
                "Typed user clarification for ordinary chat routes.");
            options.AddToolSet(
                ToolSetNames.WebRuntime,
                [CreateToolSource<WebAgentToolSource>],
                "Canonical web_search and web_fetch runtime tools.");
            options.AddToolSet(
                ToolSetNames.SkillRuntime,
                [
                    CreateToolSource<SkillsAgentToolSource>,
                    CreateToolSource<OrnnSearchAgentToolSource>,
                ],
                "Runtime skill discovery and exact skill execution.");
            options.AddToolSet(
                ToolSetNames.SkillAuthoring,
                [CreateToolSource<OrnnAuthoringAgentToolSource>],
                "Opt-in Ornn skill publishing and update tools.");
            options.AddToolSet(
                ToolSetNames.AevatarInvoke,
                [
                    CreateToolSource<InvokeGAgentToolSource>,
                    CreateToolSource<InvokeTeamToolSource>,
                    CreateToolSource<InvokeMemberToolSource>,
                    CreateToolSource<StartWorkflowToolSource>,
                    CreateToolSource<WorkflowCatalogAgentToolSource>,
                ],
                "Typed Aevatar agent, team, member, and workflow invocation tools.");
            options.AddToolSet(
                ToolSetNames.AevatarObserve,
                [
                    CreateToolSource<ObserveRunToolSource>,
                    CreateToolSource<ReadWorkflowRunArtifactToolSource>,
                ],
                "Typed run observation and artifact reads.");
            options.AddToolSet(
                ToolSetNames.ResponsesState,
                [CreateToolSource<ResponsesAevatarToolProvider>],
                "Responses-owned state tools without ingress Web aliases.");
            options.AddToolSet(
                ToolSetNames.NyxIdPrivileged,
                [CreateToolSource<NyxIdAgentToolSource>],
                "Opt-in NyxID account, service, credential, proxy, approval, node, and admin tools.");
            options.AddToolSet(
                ToolSetNames.NyxIdExecution,
                [CreateToolSource<NyxIdExecutionAgentToolSource>],
                "Opt-in NyxID SSH, code, and Codex execution tools.");
            options.AddToolSet(
                ToolSetNames.StorageRead,
                [CreateToolSource<ChronoStorageReadAgentToolSource>],
                "Read-only ChronoStorage browsing tools.");
            options.AddToolSet(
                ToolSetNames.StorageWrite,
                [CreateToolSource<ChronoStorageWriteAgentToolSource>],
                "Mutating ChronoStorage tools.");
            options.AddToolSet(
                ToolSetNames.ChannelCore,
                [
                    CreateToolSource<ChannelInteractiveReplyToolSource>,
                    CreateToolSource<ChannelRegistrationToolSource>,
                    CreateToolSource<AgentDeliveryTargetToolSource>,
                ],
                "Channel-agnostic reply, registration, and delivery-target tools.");
            options.AddToolSet(
                ToolSetNames.ChannelLark,
                [ToolSetNames.ChannelCore],
                [CreateToolSource<LarkAgentToolSource>],
                "Lark channel tools with the shared channel core.");
            options.AddToolSet(
                ToolSetNames.ChannelTelegram,
                [ToolSetNames.ChannelCore],
                [CreateToolSource<TelegramAgentToolSource>],
                "Telegram channel tools with the shared channel core.");
            options.AddToolSet(
                ToolSetNames.WorkspaceDefault,
                [
                    ToolSetNames.ChatCore,
                    ToolSetNames.WebRuntime,
                    ToolSetNames.SkillRuntime,
                    ToolSetNames.AevatarInvoke,
                    ToolSetNames.AevatarObserve,
                ],
                [],
                "Public text route ceiling composed from reviewed runtime capabilities.");
            options.AddToolSet(
                ToolSetNames.LarkSelfNotify,
                [ToolSetNames.WorkspaceDefault, ToolSetNames.ChannelLark],
                [],
                "Explicit Lark route composition with the public workspace ceiling.");
            options.AddToolSet(
                ToolSetNames.StudioLocal,
                [
                    CreateToolSource<ProvisionWorkflowScheduleToolSource>,
                    CreateToolSource<CreateStudioTeamToolSource>,
                    CreateToolSource<StudioTeamQueryToolSource>,
                    CreateToolSource<CreateStudioMemberToolSource>,
                    CreateToolSource<CreateStudioMemberWorkflowDraftToolSource>,
                    CreateToolSource<StudioMemberQueryToolSource>,
                    CreateToolSource<StudioMemberInvocationReadinessToolSource>,
                    CreateToolSource<StudioWorkflowQueryToolSource>,
                    CreateToolSource<StudioScheduleQueryToolSource>,
                    CreateToolSource<BindStudioMemberWorkflowToolSource>,
                    CreateToolSource<ScheduleStudioMemberWorkflowToolSource>,
                ],
                "Studio-owned local provisioning, member, binding, schedule, and query tools.");
            options.AddToolSet<WorkflowExternalCapabilityAuthoringToolSource>(
                ToolSetNames.WorkflowExternalCapabilityAuthoring,
                "Read-only external workflow capability discovery, readiness, and explicit-request preview.");
            // Opt-in only: connected-service tools carry per-user NyxID surfaces, so this set
            // is referenced by route policy (not folded into workspace.default) to avoid
            // injecting every caller's connected services by default.
            options.AddToolSet(
                ToolSetNames.NyxIdConnectedServices,
                [CreateToolSource<NyxIdConnectedServiceToolSource>],
                "NyxID request-local operations admitted from the exact MCP and connected-service inventory intersection.");
            options.AddToolSet(
                ToolSetNames.NyxIdAssistantAdmission,
                [CreateToolSource<NyxIdAssistantToolSource>],
                "Pinned local NyxID Assistant tools used by built-in admission intents without external discovery dependencies.");
            options.AddToolSet(
                ToolSetNames.NyxIdChatBaseline,
                [
                    CreateToolSource<NyxIdAssistantToolSource>,
                    CreateToolSource<AskUserAgentToolSource>,
                    CreateToolSource<SkillsAgentToolSource>,
                    CreateToolSource<OrnnSearchAgentToolSource>,
                ],
                "Reviewed unprofiled NyxID chat baseline: pinned Class-R management reads, the " +
                "service readiness gate, typed user input, and explicit skill discovery/loading.");
            options.AddToolSet(
                ToolSetNames.NyxIdChatDefault,
                [
                    CreateToolSource<NyxIdAssistantToolSource>,
                    CreateToolSource<NyxIdConnectedServiceToolSource>,
                    CreateToolSource<WebSearchAgentToolSource>,
                    CreateToolSource<AskUserAgentToolSource>,
                    CreateToolSource<ConditionEvaluateAgentToolSource>,
                    CreateToolSource<SkillsAgentToolSource>,
                    CreateToolSource<OrnnSearchAgentToolSource>,
                    CreateToolSource<OrnnPublishAgentToolSource>,
                    CreateToolSource<StartWorkflowToolSource>,
                    CreateToolSource<ObserveRunToolSource>,
                    CreateToolSource<ReadWorkflowRunArtifactToolSource>,
                ],
                "Ordinary NyxID Assistant turn surface: safe management reads, admitted request-local connected-service operations, web and Ornn skill search, readiness, typed user input, explicit skill loading, and managed workflow execution with typed observation.");
            options.AddToolSet(
                AgentProfilePolicies.NyxIdChatRouteToolSet,
                [
                    ToolSetNames.NyxIdChatDefault,
                    ToolSetNames.WorkflowExternalCapabilityAuthoring,
                ],
                [],
                "NyxID Agent Profile authority route. Per-turn intent policy attenuates this superset before schemas reach the model.");
        });

        return builder;
    }

    private static void AddNyxIdChatAgentProfile(WebApplicationBuilder builder)
    {
        builder.Services.Replace(
            ServiceDescriptor.Singleton<INyxIdChatAgentProfileResolver,
                MainnetNyxIdChatAgentProfileResolver>());
    }

    private static void ReplaceMainnetWorkflowAgentToolSourceAdapter(IServiceCollection services)
    {
        var defaultAdapter = services.SingleOrDefault(static descriptor =>
            descriptor.ServiceType == typeof(IWorkflowToolSource) &&
            descriptor.ImplementationType == typeof(AgentWorkflowToolSourceAdapter));
        if (defaultAdapter is null)
        {
            throw new InvalidOperationException(
                "The default workflow agent tool source adapter registration is missing.");
        }

        services.Remove(defaultAdapter);
        services.AddSingleton<IWorkflowToolSource>(serviceProvider =>
            new AgentWorkflowToolSourceAdapter(
                serviceProvider.GetServices<IAgentToolSource>()
                    .Append(serviceProvider.GetRequiredService<NyxIdWorkflowAgentToolSource>())
                    .Append(serviceProvider.GetRequiredService<NyxIdExecutionAgentToolSource>())
                    .ToArray(),
                serviceProvider.GetRequiredService<IAgentToolExecutionPort>(),
                serviceProvider.GetRequiredService<ILogger<AgentWorkflowToolSourceAdapter>>()));
    }

    private static IAgentToolSource CreateToolSource<TSource>(IServiceProvider serviceProvider)
        where TSource : class, IAgentToolSource
        => ActivatorUtilities.CreateInstance<TSource>(serviceProvider);

    public static WebApplication MapAevatarMainnetHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseAIWorkspaceErrorContract();
        app.UseAevatarDefaultHost();
        app.MapMainnetChatEndpoints();
        app.MapNyxIdChatPublicEndpoints();
        app.MapNyxIdChatEndpoints();
        app.MapChatRoutePolicyAdminEndpoints();
        app.MapLLMModelCatalogEndpoints();
        app.MapAgentProfileEndpoints();
        app.MapAIWorkspaceEndpoints();
        app.MapAIWorkspaceAgentManagementEndpoints();
        app.MapAIPageEndpoints();
        app.MapDefaultVoiceAgentEndpoints();
        app.MapVoicePresenceCapabilityAdminEndpoints();
        app.MapVoiceConsoleEndpoints();
        app.MapAutoConsoleCallbackEndpoints();
        app.MapAdminConsoleEndpoints();
        app.MapDeliveryConsoleEndpoints();
        app.MapCqrsObservatoryPageEndpoints();
        app.MapCqrsObservatoryApiEndpoints();
        app.MapCqrsProjectionFailureRepairAdminEndpoints();
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

        // NyxID access tokens use its public API BASE_URL as their audience. Identity assertions use
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

    private static string? FirstConfiguredValue(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static void ConfigureMainnetAIFeatures(AevatarAIFeatureOptions options)
    {
        options.EnableBindingTools = true;
        options.EnableWorkflowExternalCapabilityAuthoringTools = true;

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

internal static class MainnetDeterministicComputeConnectorDefinition
{
    internal const string ConnectorName = "deterministic_compute";

    internal static ConnectorConfigEntry CreateRuntimeDefinition() =>
        new()
        {
            Name = ConnectorName,
            Type = "host_callback",
            Enabled = true,
            TimeoutMs = 30_000,
            Retry = 0,
            HostCallback = new HostCallbackConnectorConfig
            {
                Handler = SHA256DeterministicComputeHandler.HandlerName,
                AllowedOperations = [SHA256DeterministicComputeHandler.OperationId],
                AllowedInputKeys = ["text"],
            },
        };

    internal static StoredConnectorDefinition CreateCatalogDefinition() =>
        new(
            Name: ConnectorName,
            Type: "host_callback",
            Enabled: true,
            TimeoutMs: 30_000,
            Retry: 0,
            Http: new StoredHttpConnectorConfig(
                string.Empty,
                [],
                [],
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                EmptyAuth()),
            Cli: new StoredCliConnectorConfig(
                string.Empty,
                [],
                [],
                [],
                string.Empty,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            Mcp: new StoredMcpConnectorConfig(
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                EmptyAuth(),
                string.Empty,
                [],
                []),
            HostCallback: new StoredHostCallbackConnectorConfig(
                SHA256DeterministicComputeHandler.HandlerName,
                [SHA256DeterministicComputeHandler.OperationId],
                ["text"]));

    private static StoredConnectorAuthConfig EmptyAuth() =>
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
}

internal sealed class MainnetDeterministicComputeConnectorCatalogDefaults : IHostConnectorCatalogDefaults
{
    public IReadOnlyList<StoredConnectorDefinition> Connectors { get; } =
        [MainnetDeterministicComputeConnectorDefinition.CreateCatalogDefinition()];
}

internal sealed class MainnetDeterministicComputeConnectorHostedService : IHostedService
{
    private readonly IConnectorRegistry _registry;
    private readonly IReadOnlyList<IConnectorBuilder> _connectorBuilders;
    private readonly ILogger<MainnetDeterministicComputeConnectorHostedService> _logger;

    public MainnetDeterministicComputeConnectorHostedService(
        IConnectorRegistry registry,
        IEnumerable<IConnectorBuilder> connectorBuilders,
        ILogger<MainnetDeterministicComputeConnectorHostedService> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _connectorBuilders = (connectorBuilders ?? throw new ArgumentNullException(nameof(connectorBuilders)))
            .ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Implement (issue #3542):
    //   Behavior: Register Mainnet's deterministic_compute connector independently of a node-local connectors.json.
    //   Why this shape: The existing builder remains the fail-closed authority for descriptor/config alignment.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = _connectorBuilders.FirstOrDefault(static candidate =>
            string.Equals(candidate.Type, "host_callback", StringComparison.OrdinalIgnoreCase));
        if (builder is null)
            throw new InvalidOperationException("Mainnet requires the host_callback connector builder.");

        var definition = MainnetDeterministicComputeConnectorDefinition.CreateRuntimeDefinition();
        if (!builder.TryBuild(definition, _logger, out var connector) || connector is null)
        {
            throw new InvalidOperationException(
                "Mainnet deterministic_compute connector does not match the registered algorithm descriptor.");
        }

        await _registry.RegisterAsync(
            global::Aevatar.Foundation.Abstractions.Connectors.ConnectorRegistration.Owned(connector),
            cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
