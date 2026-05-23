using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.CQRS.Core.Commands;

public sealed class StreamActorOutcomeChannel<TOutcome> : IActorOutcomeChannel<TOutcome>
    where TOutcome : IMessage, new()
{
    private const string StreamPrefix = "cqrs.actor-outcome";
    private readonly IStreamProvider _streamProvider;

    public StreamActorOutcomeChannel(IStreamProvider streamProvider)
    {
        _streamProvider = streamProvider ?? throw new ArgumentNullException(nameof(streamProvider));
    }

    public Task<ActorOutcomeSubscription<TOutcome>> SubscribeAsync(
        string commandId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ct.ThrowIfCancellationRequested();

        return SubscribeCoreAsync(commandId.Trim(), ct);
    }

    private async Task<ActorOutcomeSubscription<TOutcome>> SubscribeCoreAsync(
        string commandId,
        CancellationToken ct)
    {
        var source = new TaskCompletionSource<TOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = ct.Register(static state =>
        {
            var completion = (TaskCompletionSource<TOutcome>)state!;
            completion.TrySetCanceled();
        }, source);
        var lease = await GetStream(commandId).SubscribeAsync<TOutcome>(
            outcome =>
            {
                source.TrySetResult(outcome);
                return Task.CompletedTask;
            },
            ct);

        return new ActorOutcomeSubscription<TOutcome>(
            source.Task,
            async () =>
            {
                cancellation.Dispose();
                await lease.DisposeAsync();
            });
    }

    public Task PublishAsync(
        string commandId,
        TOutcome outcome,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(outcome);
        ct.ThrowIfCancellationRequested();

        return GetStream(commandId.Trim()).ProduceAsync(outcome, ct);
    }

    private IStream GetStream(string commandId) =>
        _streamProvider.GetStream($"{StreamPrefix}:{new TOutcome().Descriptor.FullName}:{commandId}");
}
