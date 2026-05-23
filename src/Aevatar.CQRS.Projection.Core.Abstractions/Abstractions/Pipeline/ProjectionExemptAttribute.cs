namespace Aevatar.CQRS.Projection.Core.Abstractions;

// Refactor (iter52/issue-895-provider-coverage-contract):
//   Old pattern: New current-state readmodels added ad-hoc without enforced activation provider coverage; provider creation was a convention only.
//   New principle: CI guard requires every new current-state readmodel to have an associated IProjectionActivationPlanProvider implementation + DI + test, or an explicit [ProjectionExempt] classification.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ProjectionExemptAttribute : Attribute
{
    public ProjectionExemptionCategory Category { get; set; }

    public string Reason { get; set; } = string.Empty;
}

public enum ProjectionExemptionCategory
{
    StartupBootstrap = 1,
    SessionObservation = 2,
    ArtifactNotCurrentState = 3,
    ProjectionCoreStatus = 4,
    TestOnly = 5,
    LegacyToDelete = 6,
}
