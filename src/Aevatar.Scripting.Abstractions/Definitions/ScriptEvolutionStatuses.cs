namespace Aevatar.Scripting.Abstractions.Definitions;

public static class ScriptEvolutionStatuses
{
    public const string Pending = "pending";
    public const string Proposed = "proposed";
    public const string BuildRequested = "build_requested";
    public const string Validated = "validated";
    public const string ValidationFailed = "validation_failed";
    public const string Rejected = "rejected";
    public const string PromotionFailed = "promotion_failed";
    public const string Promoted = "promoted";
    public const string RollbackRequested = "rollback_requested";
    public const string RolledBack = "rolled_back";
}
