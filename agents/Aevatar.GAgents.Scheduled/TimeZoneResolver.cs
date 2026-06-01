namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Resolves configured schedule timezone identifiers at the infrastructure boundary.
/// </summary>
public sealed class TimeZoneResolver : ITimeZoneResolver
{
    public bool TryResolve(
        string? timeZoneId,
        out TimeZoneInfo timeZone,
        out string? error)
    {
        error = null;
        var normalized = string.IsNullOrWhiteSpace(timeZoneId)
            ? SkillRunnerDefaults.DefaultTimezone
            : timeZoneId.Trim();

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(normalized);
            return true;
        }
        catch (TimeZoneNotFoundException ex)
        {
            timeZone = TimeZoneInfo.Utc;
            error = ex.Message;
            return false;
        }
        catch (InvalidTimeZoneException ex)
        {
            timeZone = TimeZoneInfo.Utc;
            error = ex.Message;
            return false;
        }
    }
}
