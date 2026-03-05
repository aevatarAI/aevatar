using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Sisyphus.Application.Endpoints;
using Sisyphus.Application.Models.Upload;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.DependencyInjection;

public static class SisyphusServiceCollectionExtensions
{
    public static WebApplicationBuilder AddSisyphusCapability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.Configure<SisyphusGraphOptions>(builder.Configuration.GetSection(SisyphusGraphOptions.SectionName));
        builder.Services.Configure<ChronoGraphOptions>(builder.Configuration.GetSection(ChronoGraphOptions.SectionName));
        builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection(UploadOptions.SectionName));

        return builder.AddAevatarCapability(
            name: "sisyphus",
            configureServices: (services, configuration) =>
            {
                services.AddSingleton<GraphIdProvider>();
                services.AddSingleton<SessionLifecycleService>();
                services.AddSingleton<WorkflowTriggerService>();
                services.AddHostedService<GraphBootstrapService>();
                services.AddSingleton<NyxIdTokenService>();
                services.AddHttpClient<ChronoGraphProxyService>();
                services.AddSingleton<TarGzParserService>();
                services.AddHttpClient<ChronoGraphWriteService>();
                services.AddSingleton<UploadPipelineService>();
            },
            mapEndpoints: app => app.MapSessionEndpoints().MapGraphEndpoints().MapUploadEndpoints());
    }
}
