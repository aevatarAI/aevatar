using Aevatar.Foundation.Abstractions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunWriteContext
{
    private readonly Func<WorkflowRunState, CancellationToken, Task> _persistStateAsync;
    private readonly Func<IMessage, EventDirection, CancellationToken, Task> _publishAsync;
    private readonly Func<string, IMessage, CancellationToken, Task> _sendToAsync;
    private readonly Func<Exception?, string, object?[], Task> _logWarningAsync;

    public WorkflowRunWriteContext(
        Func<WorkflowRunState, CancellationToken, Task> persistStateAsync,
        Func<IMessage, EventDirection, CancellationToken, Task> publishAsync,
        Func<string, IMessage, CancellationToken, Task> sendToAsync,
        Func<Exception?, string, object?[], Task> logWarningAsync)
    {
        _persistStateAsync = persistStateAsync ?? throw new ArgumentNullException(nameof(persistStateAsync));
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _sendToAsync = sendToAsync ?? throw new ArgumentNullException(nameof(sendToAsync));
        _logWarningAsync = logWarningAsync ?? throw new ArgumentNullException(nameof(logWarningAsync));
    }

    public Task PersistStateAsync(WorkflowRunState next, CancellationToken ct) =>
        _persistStateAsync(next, ct);

    public Task PublishAsync(IMessage evt, EventDirection direction, CancellationToken ct) =>
        _publishAsync(evt, direction, ct);

    public Task SendToAsync(string targetActorId, IMessage evt, CancellationToken ct) =>
        _sendToAsync(targetActorId, evt, ct);

    public Task LogWarningAsync(Exception? ex, string message, params object?[] args) =>
        _logWarningAsync(ex, message, args);
}
