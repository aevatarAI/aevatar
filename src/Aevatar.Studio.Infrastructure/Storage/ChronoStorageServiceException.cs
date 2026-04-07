using System.Net;

namespace Aevatar.Studio.Infrastructure.Storage;

public sealed class ChronoStorageServiceException : Exception
{
    public const string DefaultCode = "chrono_storage_service_unavailable";
    public const string DependencyName = "chrono-storage-service";

    public ChronoStorageServiceException(
        string message,
        HttpStatusCode? upstreamStatusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        UpstreamStatusCode = upstreamStatusCode;
    }

    public string Code => DefaultCode;

    public string Dependency => DependencyName;

    public HttpStatusCode? UpstreamStatusCode { get; }
}
