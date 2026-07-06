using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.BackendConsole.Hosting;

public static class BackendConsoleAssetHttpContextExtensions
{
    public static IResult ServeBackendConsoleAsset(this HttpContext http, BackendConsoleAsset asset)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(asset);

        var service = http.RequestServices.GetService<IBackendConsoleAssetService>()
            ?? BuildFallbackAssetService(http);
        return service.Serve(asset);
    }

    private static IBackendConsoleAssetService BuildFallbackAssetService(HttpContext http)
    {
        var configuration = http.RequestServices.GetRequiredService<IConfiguration>();
        var options = BackendConsoleHostingServiceCollectionExtensions.BuildOptions(configuration);
        return new BackendConsoleAssetService(Options.Create(options));
    }
}
