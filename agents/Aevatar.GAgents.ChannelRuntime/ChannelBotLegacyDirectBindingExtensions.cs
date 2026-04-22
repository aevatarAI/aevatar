namespace Aevatar.GAgents.ChannelRuntime;

internal static class ChannelBotLegacyDirectBindingExtensions
{
    public static string GetNyxUserToken(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveLegacyDirectBinding()?.NyxUserToken ?? string.Empty;

    public static string GetNyxRefreshToken(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveLegacyDirectBinding()?.NyxRefreshToken ?? string.Empty;

    public static string GetVerificationToken(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveLegacyDirectBinding()?.VerificationToken ?? string.Empty;

    public static string GetCredentialRef(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveLegacyDirectBinding()?.CredentialRef ?? string.Empty;

    public static string GetEncryptKey(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveLegacyDirectBinding()?.EncryptKey ?? string.Empty;
}
