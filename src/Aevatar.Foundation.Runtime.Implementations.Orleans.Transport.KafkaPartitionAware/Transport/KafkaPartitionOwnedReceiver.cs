namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

internal sealed class KafkaPartitionOwnedReceiver : IPartitionOwnedReceiver
{
    private static readonly TimeSpan DefaultDrainPollDelay = TimeSpan.FromMilliseconds(50);
    private readonly int _partitionId;
    private readonly IKafkaPartitionAwareEnvelopeTransport _transport;
    private readonly ILocalDeliveryAckPort _localDeliveryAckPort;
    private readonly CancellationTokenSource _closingCts = new();

    private IAsyncDisposable? _subscription;
    private int _closing;
    private int _inFlight;

    public KafkaPartitionOwnedReceiver(
        int partitionId,
        IKafkaPartitionAwareEnvelopeTransport transport,
        ILocalDeliveryAckPort localDeliveryAckPort)
    {
        _partitionId = partitionId;
        _transport = transport;
        _localDeliveryAckPort = localDeliveryAckPort;
    }

    public int PartitionId => _partitionId;

    public async Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_subscription != null)
            return;

        _subscription = await _transport.SubscribePartitionRecordsAsync(_partitionId, HandleRecordAsync, ct);
    }

    public Task BeginClosingAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _closing, 1);
        _closingCts.Cancel();
        return Task.CompletedTask;
    }

    public async Task DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(DefaultDrainPollDelay, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closingCts.Cancel();

        if (_subscription == null)
        {
            _closingCts.Dispose();
            return;
        }

        await _subscription.DisposeAsync();
        _subscription = null;
        _closingCts.Dispose();
    }

    private async Task HandleRecordAsync(PartitionEnvelopeRecord record)
    {
        if (Volatile.Read(ref _closing) == 1)
        {
            throw new PartitionRecordHandoffAbortedException(
                _partitionId,
                "the owned receiver is closing and the record must be replayed on the next owner");
        }

        Interlocked.Increment(ref _inFlight);
        try
        {
            await _localDeliveryAckPort.DeliverAsync(_partitionId, record, _closingCts.Token);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _closing) == 1 || _closingCts.IsCancellationRequested)
        {
            throw new PartitionRecordHandoffAbortedException(
                _partitionId,
                "the owned receiver stopped while the local handoff was still in flight");
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
