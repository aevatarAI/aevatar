namespace Aevatar.Scripting.Infrastructure.Ports;

internal static class ScriptingTimeoutValueNormalizer
{
    public static TimeSpan NormalizeOrDefault(TimeSpan timeout, TimeSpan defaultTimeout) =>
        timeout > TimeSpan.Zero
            ? timeout
            : defaultTimeout;
}
