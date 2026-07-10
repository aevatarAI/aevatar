using System.Threading.Channels;
using Aevatar.GAgentService.Abstractions.Responses;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Application.Responses;

// Bounded, in-process hand-off queue for off-grain LLM run execution. Enqueue is a
// non-blocking TryWrite: it never awaits a full channel, so it cannot occupy the session
// actor's scheduling turn. A full queue throws LlmRunExecutionQueueFullException, which the
// session actor turns into an execution_dispatch_failed terminal (honest fast-fail under
// overload instead of unbounded memory growth or a blocked grain).
public sealed class LlmRunExecutionQueue : ILlmRunExecutionQueue
{
    private readonly Channel<LlmRunExecutionRequest> _channel;

    public LlmRunExecutionQueue(IOptions<LlmRunExecutionWorkerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capacity = options.Value.QueueCapacity > 0 ? options.Value.QueueCapacity : 1;
        _channel = Channel.CreateBounded<LlmRunExecutionRequest>(new BoundedChannelOptions(capacity)
        {
            // We only ever TryWrite (never WriteAsync), so FullMode governs nothing here;
            // TryWrite returns false when the queue is at capacity.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Enqueue(LlmRunExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_channel.Writer.TryWrite(request))
        {
            throw new LlmRunExecutionQueueFullException(
                $"LLM run execution queue is full for response '{request.ResponseId}' run '{request.RunId}'.");
        }
    }

    public IAsyncEnumerable<LlmRunExecutionRequest> DequeueAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);
}
