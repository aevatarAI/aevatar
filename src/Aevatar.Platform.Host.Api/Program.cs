using Aevatar.Configuration;
using Aevatar.Platform.Host.Api.Endpoints;
using Aevatar.Platform.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddAevatarConfig();
builder.Host.UsePlatformCqrsRuntime(builder.Configuration);
builder.Services.AddPlatformSubsystem(builder.Configuration);

var app = builder.Build();

app.MapPlatformEndpoints();
app.MapPlatformCommandEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar.Platform.Host.Api", status = "running" }));

app.Run();
