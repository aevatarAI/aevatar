namespace Aevatar.Configuration;

/// <summary>Shared policy for bounded failover from an internal NyxID transport.</summary>
public static class NyxIdTransportFallbackPolicy
{
    public const string TimeoutSecondsConfigurationKey =
        "Aevatar:NyxId:InternalApiFallbackTimeoutSeconds";

    public const int DefaultTimeoutSeconds = 5;

    // Leave at least 30 seconds of the 330-second NyxID transport ceiling for the public attempt.
    public const int MaximumTimeoutSeconds = 300;

    public static TimeSpan EffectiveTimeout(int configuredSeconds) =>
        TimeSpan.FromSeconds(NormalizeTimeoutSeconds(configuredSeconds));

    public static int NormalizeTimeoutSeconds(int configuredSeconds) =>
        configuredSeconds <= 0
            ? DefaultTimeoutSeconds
            : Math.Min(configuredSeconds, MaximumTimeoutSeconds);

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
