using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Extensions.Maker;

var builder = WebApplication.CreateBuilder(args);

builder.AddAevatarDefaultHost(
    configureBootstrap: options =>
    {
        options.EnableMEAIProviders = true;
        options.EnableMCPTools = true;
        options.EnableSkills = true;
    },
    configureHost: options =>
    {
        options.ServiceName = "Aevatar.Mainnet.Host.Api";
        options.EnableWebSockets = true;
    });
builder.AddWorkflowCapability();
builder.Services.AddWorkflowMakerExtensions();

var app = builder.Build();

app.UseAevatarDefaultHost();

app.Run();
