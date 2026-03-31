using Aevatar.Authentication.Hosting;
using Aevatar.Authentication.Providers.NyxId;
using Aevatar.Bootstrap.Hosting;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.NyxId.Chat;
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
builder.Services.AddNyxIdChat();
builder.Services.AddNyxIdTools(o =>
{
    o.BaseUrl = builder.Configuration["Aevatar:NyxId:Authority"]
                ?? builder.Configuration["Cli:App:NyxId:Authority"]
                ?? builder.Configuration["Aevatar:Authentication:Authority"];
});

var app = builder.Build();

app.UseAevatarDefaultHost();
app.MapNyxIdChatEndpoints();

app.Run();
