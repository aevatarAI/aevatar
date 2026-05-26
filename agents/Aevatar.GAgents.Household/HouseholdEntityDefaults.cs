namespace Aevatar.GAgents.Household;

/// <summary>
/// Constants for HouseholdEntity trigger thresholds and safety limits.
/// </summary>
internal static class HouseholdEntityDefaults
{
    // ─── Trigger thresholds ───
    public const double TemperatureChangeThreshold = 2.0;   // °C
    public const double LightLevelChangeThreshold = 0.30;   // 30%
    public const int HeartbeatIntervalSeconds = 600;         // 10 minutes
    public const int ReasoningDebounceSeconds = 30;

    // ─── Safety limits ───
    public const int MaxActionsPerMinute = 3;
    public const int MaxRecentActions = 20;
    public const int MaxMemories = 50;

    // ─── AI config defaults ───
    public const string DefaultProviderName = "nyxid";
    public const int DefaultMaxToolRounds = 5;
    public const int DefaultMaxHistoryMessages = 20;

    // ─── Time periods ───
    public static string GetTimePeriod(int hour) => hour switch
    {
        >= 6 and < 12 => "morning",
        >= 12 and < 18 => "afternoon",
        >= 18 and < 22 => "evening",
        _ => "night",
    };
}
