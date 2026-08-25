using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Foundation.Projection.Runtime;

public static class RuntimeFleetCapabilityProjectionKinds
{
    public const string AuthorityCurrentState =
        "runtime-fleet-capability-authority-current-state";
}

public sealed class RuntimeFleetCapabilityProjectionContext
    : IProjectionMaterializationContext
{
    public required string RootActorId { get; init; }

    public required string ProjectionKind { get; init; }
}

public sealed class RuntimeFleetCapabilityProjectionRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<RuntimeFleetCapabilityProjectionContext>
{
    public RuntimeFleetCapabilityProjectionRuntimeLease(
        RuntimeFleetCapabilityProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
    }

    public RuntimeFleetCapabilityProjectionContext Context { get; }
}
