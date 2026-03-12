using Aevatar.CQRS.Core.Abstractions.Commands;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Core.Commands;

public sealed class FallbackCommandDispatchService<TCommand, TReceipt, TError>
    : ICommandDispatchService<TCommand, TReceipt, TError>
{
    private readonly ICommandDispatchService<TCommand, TReceipt, TError> _inner;
    private readonly ICommandFallbackPolicy<TCommand> _fallbackPolicy;
    private readonly ILogger<FallbackCommandDispatchService<TCommand, TReceipt, TError>> _logger;

    public FallbackCommandDispatchService(
        ICommandDispatchService<TCommand, TReceipt, TError> inner,
        ICommandFallbackPolicy<TCommand> fallbackPolicy,
        ILogger<FallbackCommandDispatchService<TCommand, TReceipt, TError>>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _fallbackPolicy = fallbackPolicy ?? throw new ArgumentNullException(nameof(fallbackPolicy));
        _logger = logger ?? NullLogger<FallbackCommandDispatchService<TCommand, TReceipt, TError>>.Instance;
    }

    public async Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            return await _inner.DispatchAsync(command, ct);
        }
        catch (Exception ex) when (_fallbackPolicy.TryCreateFallbackCommand(command, ex, out var fallbackCommand))
        {
            _logger.LogWarning(
                ex,
                "Command dispatch failed and falls back to alternate command path. command={CommandType}",
                typeof(TCommand).FullName);
            return await _inner.DispatchAsync(fallbackCommand, ct);
        }
    }
}
