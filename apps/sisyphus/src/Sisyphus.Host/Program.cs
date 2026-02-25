using Aevatar.Bootstrap.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Extensions.Maker;
using Sisyphus.Application.DependencyInjection;
using Sisyphus.Host.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAevatarDefaultHost(
    configureHost: options =>
    {
        options.ServiceName = "Sisyphus.Host";
        options.EnableWebSockets = true;
    });
builder.AddSisyphusOrleansHost();
builder.AddWorkflowCapabilityWithAIDefaults();
builder.Services.AddWorkflowMakerExtensions();
builder.AddSisyphusCapability();

var app = builder.Build();

app.UseAevatarDefaultHost();

app.Run();
