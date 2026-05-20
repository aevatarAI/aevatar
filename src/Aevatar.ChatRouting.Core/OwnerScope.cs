namespace Aevatar.ChatRouting.Core;

/// <summary>
/// Caller ownership tuple used by chat routing read ports.
/// Mirrors Aevatar.GAgents.Scheduled.OwnerScope until #700 moves the shared
/// contract into Foundation.Abstractions.
/// </summary>
public sealed class OwnerScope
{
    public const string NyxIdPlatform = "nyxid";

    public string NyxUserId { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string RegistrationScopeId { get; init; } = string.Empty;

    public string SenderId { get; init; } = string.Empty;

    public static OwnerScope ForNyxIdNative(string nyxUserId) =>
        new()
        {
            NyxUserId = nyxUserId ?? string.Empty,
            Platform = NyxIdPlatform,
        };

    public static OwnerScope ForChannel(
        string nyxUserId,
        string platform,
        string registrationScopeId,
        string senderId) =>
        new()
        {
            NyxUserId = nyxUserId ?? string.Empty,
            Platform = (platform ?? string.Empty).Trim().ToLowerInvariant(),
            RegistrationScopeId = registrationScopeId ?? string.Empty,
            SenderId = senderId ?? string.Empty,
        };
}
