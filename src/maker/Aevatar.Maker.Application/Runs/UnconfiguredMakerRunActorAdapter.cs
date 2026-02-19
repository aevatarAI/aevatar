using Aevatar.Foundation.Abstractions;
using Aevatar.Maker.Application.Abstractions.Runs;

namespace Aevatar.Maker.Application.Runs;

internal sealed class UnconfiguredMakerRunActorAdapter : IMakerRunActorAdapter
{
    private static InvalidOperationException CreateError() =>
        new("IMakerRunActorAdapter is not configured. Register maker infrastructure adapter.");

    public Task<MakerResolvedActor> ResolveOrCreateAsync(
        IActorRuntime runtime,
        string? actorId,
        CancellationToken ct = default) =>
        throw CreateError();

    public Task ConfigureAsync(
        IActor actor,
        MakerRunRequest request,
        CancellationToken ct = default) =>
        throw CreateError();

    public EventEnvelope CreateStartEnvelope(MakerRunRequest request, string correlationId) =>
        throw CreateError();

    public bool TryResolveCompletion(EventEnvelope envelope, out MakerRunCompletion completion)
    {
        completion = default!;
        throw CreateError();
    }
}
