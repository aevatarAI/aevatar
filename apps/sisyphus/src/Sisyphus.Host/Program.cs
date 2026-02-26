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

builder.Services.AddCors(options =>
{
    options.AddPolicy("DevCors", policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseCors("DevCors");
}

app.UseAevatarDefaultHost();

app.Run();
