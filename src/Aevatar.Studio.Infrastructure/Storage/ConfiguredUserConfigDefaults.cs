using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Infrastructure.Storage;

internal sealed class ConfiguredUserConfigDefaults : IUserConfigDefaults
{
    public ConfiguredUserConfigDefaults(IOptions<StudioStorageOptions> studioStorageOptions)
    {
        var resolvedOptions = (studioStorageOptions?.Value ?? throw new ArgumentNullException(nameof(studioStorageOptions)))
            .ResolveRootDirectory();
        LocalRuntimeBaseUrl = resolvedOptions.ResolveDefaultLocalRuntimeBaseUrl();
        RemoteRuntimeBaseUrl = resolvedOptions.ResolveDefaultRemoteRuntimeBaseUrl();
    }

    public string LocalRuntimeBaseUrl { get; }

    public string RemoteRuntimeBaseUrl { get; }
}
