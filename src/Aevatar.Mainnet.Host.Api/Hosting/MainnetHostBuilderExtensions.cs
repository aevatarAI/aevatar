using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.Lark;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.Providers.NyxId;
using Aevatar.Bootstrap.Hosting;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.GAgents.ChannelRuntime;
using Aevatar.GAgents.ChatbotClassifier;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Studio.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
        builder.Services.AddStreamingProxy();
        builder.Services.AddChatbotClassifier();
        builder.Services.AddChannelRuntime(builder.Configuration);
        builder.Services.Configure<DeviceEventOptions>(
            builder.Configuration.GetSection("Aevatar:DeviceEvents"));
        builder.Services.AddNyxIdTools(o =>
        {
            o.BaseUrl = builder.Configuration["Aevatar:NyxId:Authority"]
                        ?? builder.Configuration["Cli:App:NyxId:Authority"]
                        ?? builder.Configuration["Aevatar:Authentication:Authority"];
        });
        builder.Services.AddLarkTools(o =>
        {
            o.ProviderSlug = builder.Configuration["Aevatar:Lark:NyxProviderSlug"] ?? "api-lark-bot";
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

        return app;
    }
}
