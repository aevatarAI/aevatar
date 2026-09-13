namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>Reports an invalid NyxID execution inventory without retaining provider data or credentials.</summary>
public sealed class NyxIdServiceInventoryContractException : InvalidOperationException
{
    /// <summary>The stable error code for an invalid execution inventory contract.</summary>
    public const string ErrorCode = "NYXID_SERVICE_INVENTORY_CONTRACT_INVALID";

    /// <summary>Creates a bounded contract failure with no provider response attached.</summary>
    public NyxIdServiceInventoryContractException() : base(ErrorCode)
    {
    }
}
