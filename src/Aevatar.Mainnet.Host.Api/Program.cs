using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Workflow.Infrastructure.CapabilityApi;

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

var app = builder.Build();

app.UseAevatarDefaultHost();

app.Run();
