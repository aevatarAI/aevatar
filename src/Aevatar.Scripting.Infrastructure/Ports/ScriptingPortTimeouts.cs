namespace Aevatar.Scripting.Infrastructure.Ports;

internal static class ScriptingPortTimeouts
{
    public static TimeSpan NormalizeOrDefault(
        TimeSpan timeout,
        int defaultSeconds = 45) =>
        timeout > TimeSpan.Zero
            ? timeout
            : TimeSpan.FromSeconds(defaultSeconds);
}
