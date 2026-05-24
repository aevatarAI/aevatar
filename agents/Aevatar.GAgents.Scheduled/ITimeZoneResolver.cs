namespace Aevatar.GAgents.Scheduled;

public interface ITimeZoneResolver
{
    bool TryResolve(
        string? timeZoneId,
        out TimeZoneInfo timeZone,
        out string? error);
}
