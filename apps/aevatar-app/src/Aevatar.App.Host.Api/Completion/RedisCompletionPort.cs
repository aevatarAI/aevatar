using System.Collections.Concurrent;
using Aevatar.App.Application.Completion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Aevatar.App.Host.Api.Completion;

public sealed class RedisCompletionPort : ICompletionPort, IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiters = new();
    private readonly ISubscriber _subscriber;
    private readonly TimeSpan _timeout;
    private readonly string _channel;
    private readonly ILogger<RedisCompletionPort> _logger;

    public RedisCompletionPort(
        IConnectionMultiplexer redis,
        IOptions<CompletionPortOptions> options,
        ILogger<RedisCompletionPort> logger)
    {
        var opts = options.Value;
        _timeout = opts.Timeout;
        _channel = opts.Channel;
        _logger = logger;
        _subscriber = redis.GetSubscriber();
        _subscriber.Subscribe(RedisChannel.Literal(_channel), OnMessage);
    }

    public async Task WaitAsync(string completionKey, CancellationToken ct)
    {
        var tcs = _waiters.GetOrAdd(completionKey,
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await tcs.Task.WaitAsync(_timeout, ct);
            _logger.LogDebug("WaitAsync resolved. key={Key}", completionKey);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("WaitAsync timed out. key={Key} timeout={Timeout}s pendingCount={Count}",
                completionKey, _timeout.TotalSeconds, _waiters.Count);
            throw;
        }
        finally
        {
            _waiters.TryRemove(completionKey, out _);
        }
    }

    public void Complete(string completionKey)
    {
        var localHit = _waiters.TryRemove(completionKey, out var tcs);
        if (localHit)
            tcs!.TrySetResult(true);

        _subscriber.Publish(RedisChannel.Literal(_channel), completionKey, CommandFlags.FireAndForget);

        _logger.LogDebug("Complete fired. key={Key} localHit={LocalHit} channel={Channel}",
            completionKey, localHit, _channel);
    }

    private void OnMessage(RedisChannel channel, RedisValue message)
    {
        var key = message.ToString();
        if (_waiters.TryRemove(key, out var tcs))
        {
            tcs.TrySetResult(true);
            _logger.LogDebug("OnMessage resolved remote waiter. key={Key}", key);
        }
    }

    public void Dispose() =>
        _subscriber.Unsubscribe(RedisChannel.Literal(_channel));
}
