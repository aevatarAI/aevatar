using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Composed channel pipeline. Runs registered <see cref="IChannelMiddleware"/> instances in
/// registration order, then calls <paramref name="terminal" /> as the innermost delegate.
/// </summary>
/// <remarks>
/// Middleware can short-circuit by not invoking the <c>next</c> delegate, or wrap it with span /
/// try-catch / timing logic. Exceptions propagate to the caller so the durable inbox observer can
/// translate them into redelivery.
/// </remarks>
public sealed class ChannelPipeline
{
    private readonly IReadOnlyList<IChannelMiddleware> _middlewares;

    internal ChannelPipeline(IReadOnlyList<IChannelMiddleware> middlewares)
    {
        _middlewares = middlewares;
    }

    /// <summary>
    /// Gets the middlewares in registration order. Exposed for diagnostics / tests.
    /// </summary>
    public IReadOnlyList<IChannelMiddleware> Middlewares => _middlewares;

    /// <summary>
    /// Invokes the pipeline end-to-end.
    /// </summary>
    public Task InvokeAsync(ITurnContext context, Func<Task> terminal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(terminal);

        Func<Task> next = terminal;
        for (var i = _middlewares.Count - 1; i >= 0; i--)
        {
            var middleware = _middlewares[i];
            var capturedNext = next;
            next = () => middleware.InvokeAsync(context, capturedNext, ct);
        }
        return next();
    }
}
