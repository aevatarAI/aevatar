using Aevatar.Bootstrap.Hosting;
using Aevatar.Mainnet.Host.Api.Hosting;
using Aevatar.Workflow.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

app.UseAevatarDefaultHost();

app.Run();
