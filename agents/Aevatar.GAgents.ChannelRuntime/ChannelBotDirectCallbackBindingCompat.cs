namespace Aevatar.GAgents.ChannelRuntime;

internal static class ChannelBotDirectCallbackBindingCompat
{
    // Root scalar secret fields remain only as replay/migration fallbacks for
    // historical event/state payloads. New writes keep credential material
    // inside DirectCallbackBinding and leave those root fields empty.
    public static ChannelBotDirectCallbackBinding? ResolveDirectCallbackBinding(this ChannelBotRegistrationEntry entry) =>
        ResolveDirectCallbackBinding(
            entry.DirectCallbackBinding,
            entry.NyxUserToken,
            entry.NyxRefreshToken,
            entry.VerificationToken,
            entry.CredentialRef,
            entry.EncryptKey);

    public static ChannelBotDirectCallbackBinding? ResolveDirectCallbackBinding(this ChannelBotRegisterCommand command) =>
        ResolveDirectCallbackBinding(
            command.DirectCallbackBinding,
            command.NyxUserToken,
            command.NyxRefreshToken,
            command.VerificationToken,
            command.CredentialRef,
            command.EncryptKey);

    public static ChannelBotDirectCallbackBinding? ResolveDirectCallbackBinding(this ChannelBotUpdateTokenCommand command) =>
        ResolveDirectCallbackBinding(
            command.DirectCallbackBinding,
            command.NyxUserToken,
            command.NyxRefreshToken,
            string.Empty,
            string.Empty,
            string.Empty);

    public static ChannelBotDirectCallbackBinding? ResolveDirectCallbackBinding(this ChannelBotTokenUpdatedEvent domainEvent) =>
        ResolveDirectCallbackBinding(
            domainEvent.DirectCallbackBinding,
            domainEvent.NyxUserToken,
            domainEvent.NyxRefreshToken,
            string.Empty,
            string.Empty,
            string.Empty);

    public static void ApplyDirectCallbackBinding(this ChannelBotRegistrationEntry entry, ChannelBotDirectCallbackBinding? binding)
    {
        binding = Clone(binding);
        entry.DirectCallbackBinding = binding;
        entry.NyxUserToken = string.Empty;
        entry.NyxRefreshToken = string.Empty;
        entry.VerificationToken = string.Empty;
        entry.CredentialRef = string.Empty;
        entry.EncryptKey = string.Empty;
    }

    public static void ApplyDirectCallbackBinding(this ChannelBotRegisterCommand command, ChannelBotDirectCallbackBinding? binding)
    {
        binding = Clone(binding);
        command.DirectCallbackBinding = binding;
        command.NyxUserToken = string.Empty;
        command.NyxRefreshToken = string.Empty;
        command.VerificationToken = string.Empty;
        command.CredentialRef = string.Empty;
        command.EncryptKey = string.Empty;
    }

    public static void ApplyDirectCallbackBinding(this ChannelBotUpdateTokenCommand command, ChannelBotDirectCallbackBinding? binding)
    {
        binding = Clone(binding);
        command.DirectCallbackBinding = binding;
        command.NyxUserToken = string.Empty;
        command.NyxRefreshToken = string.Empty;
    }

    public static void ApplyDirectCallbackBinding(this ChannelBotTokenUpdatedEvent domainEvent, ChannelBotDirectCallbackBinding? binding)
    {
        binding = Clone(binding);
        domainEvent.DirectCallbackBinding = binding;
        domainEvent.NyxUserToken = string.Empty;
        domainEvent.NyxRefreshToken = string.Empty;
    }

    private static ChannelBotDirectCallbackBinding? ResolveDirectCallbackBinding(
        ChannelBotDirectCallbackBinding? binding,
        string? fallbackNyxUserToken,
        string? fallbackNyxRefreshToken,
        string? fallbackVerificationToken,
        string? fallbackCredentialRef,
        string? fallbackEncryptKey)
    {
        var userToken = FirstNonEmpty(binding?.NyxUserToken, fallbackNyxUserToken);
        var refreshToken = FirstNonEmpty(binding?.NyxRefreshToken, fallbackNyxRefreshToken);
        var verificationToken = FirstNonEmpty(binding?.VerificationToken, fallbackVerificationToken);
        var credentialRef = FirstNonEmpty(binding?.CredentialRef, fallbackCredentialRef);
        var encryptKey = FirstNonEmpty(binding?.EncryptKey, fallbackEncryptKey);

        if (string.IsNullOrWhiteSpace(userToken) &&
            string.IsNullOrWhiteSpace(refreshToken) &&
            string.IsNullOrWhiteSpace(verificationToken) &&
            string.IsNullOrWhiteSpace(credentialRef) &&
            string.IsNullOrWhiteSpace(encryptKey))
        {
            return null;
        }

        return new ChannelBotDirectCallbackBinding
        {
            NyxUserToken = userToken,
            NyxRefreshToken = refreshToken,
            VerificationToken = verificationToken,
            CredentialRef = credentialRef,
            EncryptKey = encryptKey,
        };
    }

    private static ChannelBotDirectCallbackBinding? Clone(ChannelBotDirectCallbackBinding? binding) =>
        binding is null
            ? null
            : new ChannelBotDirectCallbackBinding
            {
                NyxUserToken = binding.NyxUserToken ?? string.Empty,
                NyxRefreshToken = binding.NyxRefreshToken ?? string.Empty,
                VerificationToken = binding.VerificationToken ?? string.Empty,
                CredentialRef = binding.CredentialRef ?? string.Empty,
                EncryptKey = binding.EncryptKey ?? string.Empty,
            };

    private static string FirstNonEmpty(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred)
            ? preferred
            : fallback ?? string.Empty;
}
