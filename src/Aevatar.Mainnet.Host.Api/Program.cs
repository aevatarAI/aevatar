using Aevatar.Bootstrap.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Extensions.Maker;

var builder = WebApplication.CreateBuilder(args);

builder.AddAevatarDefaultHost(
    configureHost: options =>
    {
        options.ServiceName = "Aevatar.Mainnet.Host.Api";
        options.EnableWebSockets = true;
    });
builder.AddWorkflowCapabilityWithAIDefaults();
builder.Services.AddWorkflowMakerExtensions();

var app = builder.Build();

app.UseAevatarDefaultHost();

app.Run();
