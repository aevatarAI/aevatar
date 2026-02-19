using Aevatar.Bootstrap;
using Aevatar.Configuration;
using Aevatar.CQRS.Runtime.Hosting.DependencyInjection;
using Aevatar.CQRS.Runtime.Hosting.Hosting;
using Aevatar.Mainnet.Application.DependencyInjection;
using Aevatar.Mainnet.Host.Api.Startup;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.CapabilityApi;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddAevatarConfig();
builder.Services.AddAevatarBootstrap(builder.Configuration, options =>
{
    options.EnableMEAIProviders = true;
    options.EnableMCPTools = true;
    options.EnableSkills = true;
});

builder.Host.UseAevatarCqrsRuntime(builder.Configuration);
builder.Services.AddAevatarCqrsRuntime(builder.Configuration);
builder.Services.AddMainnetCore(builder.Configuration);
builder.Services.AddMainnetCapability(
    builder.Configuration,
    static (services, configuration) => services.AddWorkflowCapability(configuration));
builder.Services.AddHostedService<ConnectorBootstrapHostedService>();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
builder.Services.AddCors(o => o.AddPolicy("Default", p =>
{
    if (builder.Environment.IsDevelopment())
    {
        p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        return;
    }

    if (allowedOrigins is { Length: > 0 })
    {
        p.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader();
        return;
    }

    p.SetIsOriginAllowed(_ => false);
}));

var app = builder.Build();

app.UseCors("Default");
app.UseWebSockets();
app.MapWorkflowCapabilityEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar.Mainnet.Host.Api", status = "running" }));

app.Run();
