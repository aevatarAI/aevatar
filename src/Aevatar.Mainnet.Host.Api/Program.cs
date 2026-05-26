using Aevatar.Mainnet.Host.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAevatarMainnetHost();

var app = builder.Build();

app.MapAevatarMainnetHost();

app.Run();
