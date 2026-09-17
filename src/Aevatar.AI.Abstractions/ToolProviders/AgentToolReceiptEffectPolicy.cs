namespace Aevatar.AI.Abstractions.ToolProviders;

public static class AgentToolReceiptEffectPolicy
{
    public static AgentToolReceiptEffect FromCallSafety(
        AgentToolCallSafety callSafety,
        string? sideEffectKind)
    {
        ArgumentNullException.ThrowIfNull(callSafety);

        return !callSafety.IsReadOnly ||
               callSafety.IsDestructive ||
               !string.IsNullOrWhiteSpace(sideEffectKind)
            ? AgentToolReceiptEffect.Mutating
            : AgentToolReceiptEffect.ReadOnly;
    }

    public static bool IsMutatingOrLegacyEffectCapable(AgentToolReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return receipt.Effect switch
        {
            AgentToolReceiptEffect.Mutating => true,
            AgentToolReceiptEffect.ReadOnly => false,
            _ => receipt.IsDestructive || !string.IsNullOrWhiteSpace(receipt.SideEffectKind),
        };
    }
}
