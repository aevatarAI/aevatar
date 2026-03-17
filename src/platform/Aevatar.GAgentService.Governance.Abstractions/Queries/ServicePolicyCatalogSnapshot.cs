namespace Aevatar.GAgentService.Governance.Abstractions.Queries;

public sealed record ServicePolicyCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServicePolicySnapshot> Policies,
    DateTimeOffset UpdatedAt);

public sealed record ServicePolicySnapshot(
    string PolicyId,
    string DisplayName,
    IReadOnlyList<string> ActivationRequiredBindingIds,
    IReadOnlyList<string> InvokeAllowedCallerServiceKeys,
    bool InvokeRequiresActiveDeployment,
    bool Retired);
