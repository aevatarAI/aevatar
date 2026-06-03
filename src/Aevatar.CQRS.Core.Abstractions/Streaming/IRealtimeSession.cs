namespace Aevatar.CQRS.Core.Abstractions.Streaming;

public interface IRealtimeSession<TInbound, TReceipt, TStartError, TOutboundFrame, TCompletion>
{
    Task<RealtimeSessionResult<TReceipt, TStartError, TCompletion>> ExecuteAsync(
        TInbound inbound,
        Func<TOutboundFrame, CancellationToken, ValueTask> emitAsync,
        Func<TReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}

public record RealtimeSessionResult<TReceipt, TStartError, TCompletion>
{
    public required bool Succeeded { get; init; }
    public required TStartError Error { get; init; }
    public TReceipt? Receipt { get; init; }
    public TCompletion? Completion { get; init; }
    public bool Completed { get; init; }

    public static RealtimeSessionResult<TReceipt, TStartError, TCompletion> Success(
        TReceipt receipt,
        TCompletion completion,
        bool completed)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new RealtimeSessionResult<TReceipt, TStartError, TCompletion>
        {
            Succeeded = true,
            Error = default!,
            Receipt = receipt,
            Completion = completion,
            Completed = completed,
        };
    }

    public static RealtimeSessionResult<TReceipt, TStartError, TCompletion> Failure(TStartError error) =>
        new()
        {
            Succeeded = false,
            Error = error,
            Receipt = default,
            Completion = default,
            Completed = false,
        };
}
