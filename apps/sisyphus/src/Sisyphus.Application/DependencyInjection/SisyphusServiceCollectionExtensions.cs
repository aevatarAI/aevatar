using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sisyphus.Application.Endpoints;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.DependencyInjection;

public static class SisyphusServiceCollectionExtensions
{
    public static WebApplicationBuilder AddSisyphusCapability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<NyxIdOptions>(builder.Configuration.GetSection(NyxIdOptions.SectionName));

        var nyxIdBaseUrl = builder.Configuration["NyxId:BaseUrl"] ?? "http://localhost:3001";

        return builder.AddAevatarCapability(
            name: "sisyphus",
            configureServices: (services, configuration) =>
            {
                services.AddHttpClient<NyxIdTokenService>();
                services.AddHttpClient<ChronoGraphClient>(client =>
                {
                    client.BaseAddress = new Uri(nyxIdBaseUrl);
                });
                services.AddSingleton<GraphIdProvider>();
                services.AddSingleton<SessionLifecycleService>();
                services.AddSingleton<WorkflowTriggerService>();
                services.AddHostedService<GraphBootstrapService>();
            },
            mapEndpoints: app => app.MapSessionEndpoints());
    }
}
