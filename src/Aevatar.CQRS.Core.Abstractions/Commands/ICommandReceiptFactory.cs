namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandReceiptFactory<in TTarget, out TReceipt>
    where TTarget : class, ICommandDispatchTarget
{
    TReceipt Create(
        TTarget target,
        CommandContext context);
}
