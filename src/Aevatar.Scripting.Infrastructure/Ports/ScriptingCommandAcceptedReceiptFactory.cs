using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptingCommandAcceptedReceiptFactory
    : ICommandReceiptFactory<ScriptingActorCommandTarget, ScriptingCommandAcceptedReceipt>
{
    public ScriptingCommandAcceptedReceipt Create(
        ScriptingActorCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new ScriptingCommandAcceptedReceipt(
            target.TargetId,
            context.CommandId,
            context.CorrelationId);
    }
}
