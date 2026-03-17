using Aevatar.Hosting;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.Scripting.Hosting.CapabilityApi;

public static class ScriptCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddScriptingCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddAevatarCapability(
            name: "scripting-bundle",
            configureServices: static (services, configuration) => services.AddScriptCapability(configuration),
            mapEndpoints: static app => app.MapScriptCapabilityEndpoints());
    }
}
