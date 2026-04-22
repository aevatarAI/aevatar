namespace Aevatar.GAgents.ChannelRuntime;

internal static class ChannelBotDirectCallbackBindingExtensions
{
    public static string GetNyxUserToken(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveDirectCallbackBinding()?.NyxUserToken ?? string.Empty;

    public static string GetNyxRefreshToken(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveDirectCallbackBinding()?.NyxRefreshToken ?? string.Empty;

    public static string GetVerificationToken(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveDirectCallbackBinding()?.VerificationToken ?? string.Empty;

    public static string GetCredentialRef(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveDirectCallbackBinding()?.CredentialRef ?? string.Empty;

    public static string GetEncryptKey(this ChannelBotRegistrationEntry registration) =>
        registration.ResolveDirectCallbackBinding()?.EncryptKey ?? string.Empty;
}
