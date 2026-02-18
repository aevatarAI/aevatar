using Aevatar.Configuration;
using Aevatar.Platform.Host.Api.Endpoints;
using Aevatar.CQRS.Runtime.Hosting.DependencyInjection;
using Aevatar.CQRS.Runtime.Hosting.Hosting;
using Aevatar.Platform.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddAevatarConfig();
builder.Host.UseAevatarCqrsRuntime(builder.Configuration);
builder.Services.AddAevatarCqrsRuntime(builder.Configuration);
builder.Services.AddPlatformSubsystem(builder.Configuration);

var app = builder.Build();

app.MapPlatformEndpoints();
app.MapPlatformCommandEndpoints();
app.MapGet("/", () => Results.Ok(new { name = "Aevatar.Platform.Host.Api", status = "running" }));

app.Run();
