using System.Runtime.CompilerServices;

namespace Aevatar.Foundation.Abstractions.Maintenance;

/// <summary>
/// Convenience base providing empty default implementations for the optional
/// <see cref="IRetiredActorSpec"/> members so concrete specs only need to declare
/// <see cref="SpecId"/> and <see cref="Targets"/>.
/// </summary>
public abstract class RetiredActorSpec : IRetiredActorSpec
{
    public abstract string SpecId { get; }

    public abstract IReadOnlyList<RetiredActorTarget> Targets { get; }

    public virtual async IAsyncEnumerable<RetiredActorTarget> DiscoverDynamicTargetsAsync(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public virtual Task DeleteReadModelsForActorAsync(
        IServiceProvider services,
        string actorId,
        CancellationToken ct) =>
        Task.CompletedTask;
}
