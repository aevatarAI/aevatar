using System.ComponentModel;

namespace Aevatar.Studio.Application.Delivery;

public sealed class WorkflowDeliveryOptions
{
    public const string SectionName = "Aevatar:Delivery";

    public IList<WorkflowDeliveryPackageOptions> Packages { get; set; } = [];

    // These retired keys are binding-only compatibility sinks for rolling deployments.
    // The package catalog never reads them; Packages remains the only publication authority.
    [Obsolete("Binding-only rollout compatibility; Packages is authoritative.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IList<string> AllowedWorkflowNames { get; set; } = [];

    [Obsolete("Binding-only rollout compatibility; Packages is authoritative.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool UseShippedWorkflowAllowlist { get; set; }

    [Obsolete("Binding-only rollout compatibility; ConsoleWebBaseUrl is authoritative.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string ConsoleBaseUrl { get; set; } = string.Empty;

    public string PackageDirectory { get; set; } = "workflow-delivery-packages";

    public int DefaultExpiryHours { get; set; } = 168;

    public int MaximumExpiryHours { get; set; } = 720;

    /// <summary>
    /// Absolute origin of the <c>apps/aevatar-console-web</c> product console. The delivery
    /// read model only owns the console-relative member invoke path; this origin is what
    /// turns it into a link the customer can actually open. When it is unset, delivery
    /// responses omit the console link instead of emitting a same-origin URL that resolves
    /// to the backend API host.
    /// </summary>
    public string ConsoleWebBaseUrl { get; set; } = string.Empty;
}

public sealed class WorkflowDeliveryPackageOptions
{
    public string WorkflowName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string RiskSummary { get; set; } = string.Empty;

    public IList<string> Capabilities { get; set; } = [];

    public IList<WorkflowDeliveryVariableOptions> Variables { get; set; } = [];

    public IList<WorkflowDeliveryConnectionSlotOptions> ConnectionSlots { get; set; } = [];

    public WorkflowDeliveryAcceptanceOptions Acceptance { get; set; } = new();
}

public sealed class WorkflowDeliveryVariableOptions
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryVariableKind Kind { get; set; }

    public bool Required { get; set; }

    public string YamlPointer { get; set; } = string.Empty;

    public string? JsonPointer { get; set; }

    public string? DefaultValue { get; set; }
}

public sealed class WorkflowDeliveryConnectionSlotOptions
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string ServiceSlug { get; set; } = string.Empty;

    public bool Required { get; set; }
}

public sealed class WorkflowDeliveryAcceptanceOptions
{
    public Aevatar.Studio.Application.Studio.Abstractions.WorkflowDeliveryAcceptanceMode Mode { get; set; }

    public string? Limitation { get; set; }

    public IList<WorkflowDeliveryAcceptanceInputValueOptions> Input { get; set; } = [];
}

public enum WorkflowDeliveryAcceptanceInputValueKind
{
    Unspecified = 0,
    String = 1,
    Integer = 2,
    Number = 3,
    Boolean = 4,
}

public enum WorkflowDeliveryAcceptanceInputValueSource
{
    Unspecified = 0,
    Literal = 1,
    InstallationCreatedAtUtc = 2,
    AuthenticatedOwnerExternalUserId = 3,
}

public enum WorkflowDeliveryAcceptanceDateProjection
{
    Unspecified = 0,
    UtcDate = 1,
    UtcYearMonth = 2,
    UtcIsoWeek = 3,
    UtcCompactDate = 4,
}

public sealed class WorkflowDeliveryAcceptanceInputValueOptions
{
    public string Key { get; set; } = string.Empty;

    public WorkflowDeliveryAcceptanceInputValueKind Kind { get; set; }

    public WorkflowDeliveryAcceptanceInputValueSource Source { get; set; } =
        WorkflowDeliveryAcceptanceInputValueSource.Literal;

    public string Value { get; set; } = string.Empty;

    public WorkflowDeliveryAcceptanceDateProjection DateProjection { get; set; }

    public int DayOffset { get; set; }

    public string Prefix { get; set; } = string.Empty;

    public string Suffix { get; set; } = string.Empty;
}
