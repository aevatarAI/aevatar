using Aevatar.Bootstrap.Hosting;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Extensions.Maker;
using Microsoft.Extensions.DependencyInjection;
using Sisyphus.Application.DependencyInjection;
using Sisyphus.Application.Services;
using Sisyphus.Host.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAevatarDefaultHost(
    configureHost: options =>
    {
        options.ServiceName = "Sisyphus.Host";
        options.EnableWebSockets = true;
    });
builder.AddSisyphusOrleansHost();
builder.AddWorkflowCapabilityWithAIDefaults(options =>
{
    var llmProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER");
    if (string.Equals(llmProvider, "nyx", StringComparison.OrdinalIgnoreCase))
    {
        options.AuthHandlerFactory = sp =>
        {
            var tokenService = sp.GetRequiredService<NyxIdTokenService>();
            return new NyxTokenHandler(tokenService);
        };
    }
});
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
