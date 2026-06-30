namespace Aevatar.GAgents.Platform.Lark.Abstractions;

public readonly record struct LarkReceiveTarget(
    string ReceiveId,
    string ReceiveIdType,
    bool FellBackToPrefixInference);

/// <summary>
/// Primary and fallback Lark receive target pair. Dispatchers try the primary first; on a Lark
/// <c>230002 bot not in chat</c> rejection, they may retry once with the fallback target.
/// </summary>
public readonly record struct LarkReceiveTargetWithFallback(
    LarkReceiveTarget Primary,
    LarkReceiveTarget? Fallback);
