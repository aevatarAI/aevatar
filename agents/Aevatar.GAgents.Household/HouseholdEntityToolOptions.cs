namespace Aevatar.GAgents.Household;

/// <summary>
/// Configuration for HouseholdEntity tool registration.
/// </summary>
public sealed class HouseholdEntityToolOptions
{
    /// <summary>
    /// Default actor ID prefix for household entities.
    /// Full ID = "{ActorIdPrefix}-{scope}" where scope comes from request context.
    /// </summary>
    public string ActorIdPrefix { get; set; } = "household";
}
