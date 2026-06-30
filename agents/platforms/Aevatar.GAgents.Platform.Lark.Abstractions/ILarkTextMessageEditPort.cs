namespace Aevatar.GAgents.Platform.Lark.Abstractions;

/// <summary>
/// Narrow boundary for editing one existing Lark text message.
/// </summary>
public interface ILarkTextMessageEditPort
{
    Task<LarkTextMessageEditResult> EditAsync(
        LarkTextMessageEditRequest request,
        CancellationToken ct);
}

public sealed record LarkTextMessageEditRequest(
    string NyxProxyBearerToken,
    string NyxProviderSlug,
    string MessageId,
    string Text);

public sealed record LarkTextMessageEditResult(
    bool Succeeded,
    int? LarkCode,
    string Detail)
{
    public static LarkTextMessageEditResult Success() =>
        new(true, null, string.Empty);

    public static LarkTextMessageEditResult Failed(int? larkCode, string detail) =>
        new(false, larkCode, detail);
}
