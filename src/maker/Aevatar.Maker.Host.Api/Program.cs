using Aevatar.Bootstrap;
using Aevatar.Bootstrap.Hosting;
using Aevatar.Maker.Infrastructure.CapabilityApi;
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
        options.ServiceName = "Aevatar.Maker.Host.Api";
    });
builder.AddWorkflowCapability();
builder.AddMakerCapability();

var app = builder.Build();
app.UseAevatarDefaultHost();

app.Run();
