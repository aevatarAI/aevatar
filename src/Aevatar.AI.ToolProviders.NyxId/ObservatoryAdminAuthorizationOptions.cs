namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class ObservatoryAdminAuthorizationOptions
{
    public const string ConfigSection = "Aevatar:AdminAccess";
    internal const string LegacyConfigSection = "Aevatar:Observatory";

    public string[] AllowedUserIds { get; set; } = [];

    public string[] AllowedEmails { get; set; } = [];

    public bool TrustNyxIdPlatformRole { get; set; } = true;

    public int AdminRoleCacheTtlSeconds { get; set; } = 30;

    public bool CrossScopeEnabled { get; set; } = true;
}
