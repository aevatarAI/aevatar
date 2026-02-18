using Aevatar.Bootstrap;
using Aevatar.Configuration;
using Aevatar.CQRS.Runtime.Hosting.DependencyInjection;
using Aevatar.CQRS.Runtime.Hosting.Hosting;
using Aevatar.Maker.Host.Api.Endpoints;
using Aevatar.Maker.Infrastructure.DependencyInjection;

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
builder.Services.AddMakerSubsystem(builder.Configuration);

var app = builder.Build();
app.MapMakerEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar.Maker.Host.Api", status = "running" }));

app.Run();
