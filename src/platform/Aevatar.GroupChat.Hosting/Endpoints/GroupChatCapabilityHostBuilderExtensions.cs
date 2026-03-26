using Aevatar.GroupChat.Hosting.DependencyInjection;
using Aevatar.Hosting;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.GroupChat.Hosting.Endpoints;

public static class GroupChatCapabilityHostBuilderExtensions
{
    public static WebApplicationBuilder AddGroupChatCapabilityBundle(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddAevatarCapability(
            "group-chat",
            static (services, configuration) => services.AddGroupChatCapability(configuration),
            static app => app.MapGroupChatCapabilityEndpoints());
    }
}
