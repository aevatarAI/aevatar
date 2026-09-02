using System.Threading.Channels;
using Aevatar.GAgents.WorkOrder;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Application.Studio.Services;

public interface IWorkOrderExecutionQueue
{
    void Enqueue(WorkOrderExecutionRequest request);

    void CompleteAdding();

    IAsyncEnumerable<WorkOrderExecutionRequest> DequeueAllAsync(CancellationToken ct = default);
}

public sealed class WorkOrderExecutionQueue : IWorkOrderExecutionQueue
{
    private readonly Channel<WorkOrderExecutionRequest> _channel;

    public WorkOrderExecutionQueue(IOptions<WorkOrderExecutionWorkerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capacity = options.Value.QueueCapacity > 0 ? options.Value.QueueCapacity : 1;
        _channel = Channel.CreateBounded<WorkOrderExecutionRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Enqueue(WorkOrderExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_channel.Writer.TryWrite(request.Clone()))
        {
            throw new WorkOrderExecutionQueueFullException(
                $"WorkOrder execution queue is full for work order '{request.WorkOrderId}' command '{request.DispatchCommandId}'.");
        }
    }

    public void CompleteAdding() => _channel.Writer.TryComplete();

    public IAsyncEnumerable<WorkOrderExecutionRequest> DequeueAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);
}
