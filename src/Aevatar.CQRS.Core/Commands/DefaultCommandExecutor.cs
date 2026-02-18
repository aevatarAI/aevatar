using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultCommandExecutor<TCommand>
{
    private readonly ICommandEnvelopeFactory<TCommand> _factory;

    public DefaultCommandExecutor(ICommandEnvelopeFactory<TCommand> factory)
    {
        _factory = factory;
    }

    public async Task ExecuteAsync(
        IActor actor,
        TCommand command,
        CommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var envelope = _factory.CreateEnvelope(command, context);
        await actor.HandleEventAsync(envelope, ct);
    }
}
