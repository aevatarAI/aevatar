using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.Providers.NyxId;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.AI.ToolProviders.ChronoStorage;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.ChatbotClassifier;
using Aevatar.GAgents.ChannelRuntime;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.Studio.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrWhiteSpace(builder.Configuration[WebHostDefaults.ServerUrlsKey]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
}

builder.AddAevatarDefaultHost(
    configureHost: options =>
    {
        options.ServiceName = "Aevatar.Mainnet.Host.Api";
        options.EnableWebSockets = true;
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
builder.Services.Configure<Aevatar.GAgents.ChannelRuntime.DeviceEventOptions>(
    builder.Configuration.GetSection("Aevatar:DeviceEvents"));
builder.Services.AddNyxIdTools(o =>
{
    o.BaseUrl = builder.Configuration["Aevatar:NyxId:Authority"]
                ?? builder.Configuration["Cli:App:NyxId:Authority"]
                ?? builder.Configuration["Aevatar:Authentication:Authority"];
});
builder.Services.AddChronoStorageTools(o =>
{
    // Self-referencing: the explorer endpoints are served by this same host.
    var urls = builder.Configuration[WebHostDefaults.ServerUrlsKey] ?? "http://127.0.0.1:5080";
    o.ApiBaseUrl = urls.Split(';').FirstOrDefault()?.Trim();
});

var app = builder.Build();

app.UseAevatarDefaultHost();
app.MapVoicePresenceWebSocket("/ws/voice/{actorId}");
app.MapNyxIdChatEndpoints();
app.MapStreamingProxyEndpoints();
app.MapChannelCallbackEndpoints();
app.MapDeviceEventEndpoints();

app.Run();
