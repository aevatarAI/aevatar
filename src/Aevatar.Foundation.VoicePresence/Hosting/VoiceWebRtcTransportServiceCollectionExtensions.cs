using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.Foundation.VoicePresence.Transport.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public static class VoiceWebRtcTransportServiceCollectionExtensions
{
    public static IServiceCollection AddVoiceWebRtcTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IWebRtcVoiceTransportFactory, SipsorceryWebRtcVoiceTransportFactory>();
        services.TryAddSingleton<VoiceWhipAttachExecutor>();
        return services;
    }
}
