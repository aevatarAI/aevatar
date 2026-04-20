namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IUserConfigDefaults
{
    string LocalRuntimeBaseUrl { get; }

    string RemoteRuntimeBaseUrl { get; }
}
