using Aevatar.Audit;

namespace Aevatar.Audit.Abstractions.Models;

public static class AuditContractSemantics
{
    public const string CurrentSchemaVersion = "1.0";
    public const string LegacySchemaVersion = "legacy-v0";

    public static AuditLifecyclePhase ResolveLifecyclePhase(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.LifecyclePhase != AuditLifecyclePhase.Unspecified)
            return record.LifecyclePhase;

        if (!string.IsNullOrWhiteSpace(record.SchemaVersion))
            return AuditLifecyclePhase.Unspecified;

        return record.Outcome switch
        {
            AuditOutcome.Accepted => AuditLifecyclePhase.Accepted,
            AuditOutcome.Success or AuditOutcome.Denied or AuditOutcome.Error or AuditOutcome.Cancelled =>
                AuditLifecyclePhase.Terminal,
            _ => AuditLifecyclePhase.Unspecified,
        };
    }

    public static AuditTerminalOutcome ResolveTerminalOutcome(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.TerminalOutcome != AuditTerminalOutcome.Unspecified)
            return record.TerminalOutcome;

        if (record.LifecyclePhase != AuditLifecyclePhase.Unspecified)
            return AuditTerminalOutcome.Unspecified;

        if (!string.IsNullOrWhiteSpace(record.SchemaVersion))
            return AuditTerminalOutcome.Unspecified;

        return record.Outcome switch
        {
            AuditOutcome.Success => AuditTerminalOutcome.Succeeded,
            AuditOutcome.Denied or AuditOutcome.Error => AuditTerminalOutcome.Failed,
            AuditOutcome.Cancelled => AuditTerminalOutcome.Cancelled,
            _ => AuditTerminalOutcome.Unspecified,
        };
    }

    public static AuditRecordSchemaCompatibility GetSchemaCompatibility(AuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.SchemaVersion))
            return AuditRecordSchemaCompatibility.LegacyMapped;

        return string.Equals(record.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal)
            ? AuditRecordSchemaCompatibility.Current
            : AuditRecordSchemaCompatibility.Incompatible;
    }

    public static string ResolveSchemaVersion(AuditRecord record) =>
        string.IsNullOrWhiteSpace(record.SchemaVersion)
            ? LegacySchemaVersion
            : record.SchemaVersion.Trim();
}

public enum AuditRecordSchemaCompatibility
{
    Current = 0,
    LegacyMapped = 1,
    Incompatible = 2,
}
