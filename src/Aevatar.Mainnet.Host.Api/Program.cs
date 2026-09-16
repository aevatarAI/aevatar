using Aevatar.Mainnet.Host.Api.Hosting;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services);
});

builder.AddAevatarMainnetHost();

var app = builder.Build();

app.MapAevatarMainnetHost();

app.Run();
