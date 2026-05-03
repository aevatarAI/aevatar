using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.AgentCatalog;
using Aevatar.AI.ToolProviders.Channel;
using Aevatar.AI.ToolProviders.ChannelAdmin;
using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Telegram;
using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.Providers.NyxId;
using Aevatar.Bootstrap.Hosting;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgents.Authoring.Lark;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.ChatbotClassifier;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Telegram;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Foundation.Runtime.Hosting.Maintenance;
using Aevatar.Hosting;
using Aevatar.Studio.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Mainnet.Host.Api.Hosting;

public static class MainnetHostBuilderExtensions
{
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

        if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
            builder.WebHost.UseUrls("http://127.0.0.1:5080");

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
        builder.AddAevatarPlatform(options =>
        {
            options.EnableMakerExtensions = true;
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
        // Composition root owns the ES vs InMemory store choice for the
        // Identity module: AddChannelIdentity registers actors / projector /
        // broker / slash-commands and AddChannelIdentityProjectionStores
        // wires the document store. Tests / demos can mix and match.
        builder.Services.AddChannelIdentity(builder.Configuration);
        builder.Services.AddChannelIdentityProjectionStores(builder.Configuration);
        builder.Services.AddDeviceRegistration(builder.Configuration);
        builder.Services.AddScheduledAgents(builder.Configuration);
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
        builder.Services.Configure<DeviceEventOptions>(
            builder.Configuration.GetSection("Aevatar:DeviceEvents"));
        builder.Services.AddNyxIdTools(o =>
        {
            o.BaseUrl = builder.Configuration["Aevatar:NyxId:Authority"]
                        ?? builder.Configuration["Cli:App:NyxId:Authority"]
                        ?? builder.Configuration["Aevatar:Authentication:Authority"];
            o.SpecFetchToken = builder.Configuration["Aevatar:NyxId:SpecFetchToken"];
        });
        builder.Services.AddAevatarHealthContributor(NyxIdCatalogHealthContributor.Create());
        builder.Services.AddLarkTools(o =>
        {
            o.ProviderSlug = builder.Configuration["Aevatar:Lark:NyxProviderSlug"] ?? "api-lark-bot";
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

        return builder;
    }

    public static WebApplication MapAevatarMainnetHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseAevatarDefaultHost();
        app.MapNyxIdChatEndpoints();
        app.MapStreamingProxyEndpoints();
        app.MapChannelCallbackEndpoints();
        app.MapDeviceEventEndpoints();
        app.MapIdentityOAuthEndpoints();

        return app;
    }
}
