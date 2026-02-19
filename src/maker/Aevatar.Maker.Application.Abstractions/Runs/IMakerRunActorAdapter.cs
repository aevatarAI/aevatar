using Aevatar.Foundation.Abstractions;

namespace Aevatar.Maker.Application.Abstractions.Runs;

public sealed record MakerResolvedActor(IActor Actor, bool Created);

public sealed record MakerRunCompletion(
    string Output,
    bool Success,
    string? Error);

public interface IMakerRunActorAdapter
{
    Task<MakerResolvedActor> ResolveOrCreateAsync(
        IActorRuntime runtime,
        string? actorId,
        CancellationToken ct = default);

    Task ConfigureAsync(
        IActor actor,
        MakerRunRequest request,
        CancellationToken ct = default);

    EventEnvelope CreateStartEnvelope(MakerRunRequest request, string correlationId);

    bool TryResolveCompletion(EventEnvelope envelope, out MakerRunCompletion completion);
}
