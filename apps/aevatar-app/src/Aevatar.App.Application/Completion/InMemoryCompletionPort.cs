using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Completion;

public sealed class InMemoryCompletionPort : ICompletionPort
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiters = new();
    private readonly TimeSpan _timeout;

    public InMemoryCompletionPort(IOptions<CompletionPortOptions> options)
    {
        _timeout = options.Value.Timeout;
    }

    public async Task WaitAsync(string completionKey, CancellationToken ct)
    {
        var tcs = _waiters.GetOrAdd(completionKey,
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await tcs.Task.WaitAsync(_timeout, ct);
        }
        finally
        {
            _waiters.TryRemove(completionKey, out _);
        }
    }

    public void Complete(string completionKey)
    {
        if (_waiters.TryRemove(completionKey, out var tcs))
            tcs.TrySetResult(true);
    }
}
