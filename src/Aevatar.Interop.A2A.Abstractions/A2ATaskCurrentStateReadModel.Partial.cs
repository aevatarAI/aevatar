using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Interop.A2A.Abstractions;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: tasks/get read process-local IA2ATaskStore state.
//   New principle: tasks/get reads this actor-scoped current-state readmodel only.
public sealed partial class A2ATaskCurrentStateReadModel
    : IProjectionReadModel<A2ATaskCurrentStateReadModel>
{
    public DateTimeOffset UpdatedAt => UpdatedAtUtcValue.ToDateTimeOffset();
}
