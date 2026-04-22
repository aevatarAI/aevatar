namespace Aevatar.GAgents.ChannelRuntime;

internal static class ChannelBotLegacyDirectBindingExtensions
{
    public static string GetNyxUserToken(this ChannelBotRegistrationEntry registration) =>
        registration.LegacyDirectBinding?.NyxUserToken ?? string.Empty;

    public static string GetNyxRefreshToken(this ChannelBotRegistrationEntry registration) =>
        registration.LegacyDirectBinding?.NyxRefreshToken ?? string.Empty;

    public static string GetVerificationToken(this ChannelBotRegistrationEntry registration) =>
        registration.LegacyDirectBinding?.VerificationToken ?? string.Empty;

    public static string GetCredentialRef(this ChannelBotRegistrationEntry registration) =>
        registration.LegacyDirectBinding?.CredentialRef ?? string.Empty;

    public static string GetEncryptKey(this ChannelBotRegistrationEntry registration) =>
        registration.LegacyDirectBinding?.EncryptKey ?? string.Empty;
}
