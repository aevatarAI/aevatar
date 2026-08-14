namespace Aevatar.Configuration;

/// <summary>Shared policy for bounded failover from an internal NyxID transport.</summary>
public static class NyxIdTransportFallbackPolicy
{
    public const string TimeoutSecondsConfigurationKey =
        "Aevatar:NyxId:InternalApiFallbackTimeoutSeconds";

    public const int DefaultTimeoutSeconds = 5;

    public static TimeSpan EffectiveTimeout(int configuredSeconds) =>
        TimeSpan.FromSeconds(configuredSeconds > 0 ? configuredSeconds : DefaultTimeoutSeconds);

    /// <summary>
    /// Only safe HTTP methods may be replayed after a response-header timeout. Mutations may still
    /// use the existing pre-connect fallback when the transport proves no connection was established.
    /// </summary>
    public static bool CanReplayAfterResponseHeaderTimeout(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options;
    }
}
