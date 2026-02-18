using Aevatar.Bootstrap;
using Aevatar.Configuration;
using Aevatar.CQRS.Core.DependencyInjection;
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
builder.Services.AddCqrsCore();
builder.Services.AddMakerSubsystem(builder.Configuration);

var app = builder.Build();
app.MapMakerEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar.Maker.Host.Api", status = "running" }));

app.Run();
