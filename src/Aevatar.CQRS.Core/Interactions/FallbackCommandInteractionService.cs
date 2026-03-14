using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Core.Interactions;

public sealed class FallbackCommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion>
    : ICommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion>
{
    private readonly ICommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion> _inner;
    private readonly ICommandFallbackPolicy<TCommand> _fallbackPolicy;
    private readonly ILogger<FallbackCommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion>> _logger;

    public FallbackCommandInteractionService(
        ICommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion> inner,
        ICommandFallbackPolicy<TCommand> fallbackPolicy,
        ILogger<FallbackCommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion>>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _fallbackPolicy = fallbackPolicy ?? throw new ArgumentNullException(nameof(fallbackPolicy));
        _logger = logger ?? NullLogger<FallbackCommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion>>.Instance;
    }

    public async Task<CommandInteractionResult<TReceipt, TError, TCompletion>> ExecuteAsync(
        TCommand command,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<TReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(emitAsync);

        try
        {
            return await _inner.ExecuteAsync(command, emitAsync, onAcceptedAsync, ct);
        }
        catch (Exception ex) when (_fallbackPolicy.TryCreateFallbackCommand(command, ex, out var fallbackCommand))
        {
            _logger.LogWarning(
                ex,
                "Command interaction failed and falls back to alternate command path. command={CommandType}",
                typeof(TCommand).FullName);
            return await _inner.ExecuteAsync(fallbackCommand, emitAsync, onAcceptedAsync, ct);
        }
    }
}
