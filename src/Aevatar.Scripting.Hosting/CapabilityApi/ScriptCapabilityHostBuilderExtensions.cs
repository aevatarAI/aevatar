using Aevatar.Hosting;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.Scripting.Hosting.CapabilityApi;

public static class ScriptCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddScriptCapability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddAevatarCapability(
            name: "script",
            configureServices: static (services, _) => services.AddScriptCapability(),
            mapEndpoints: static app => app.MapScriptCapabilityEndpoints());
    }
}
