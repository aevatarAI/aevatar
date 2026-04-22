namespace Aevatar.GAgents.ChannelRuntime;

internal static class ChannelBotLegacyDirectBindingCompat
{
    public static ChannelBotLegacyDirectBinding? ResolveLegacyDirectBinding(this ChannelBotRegistrationEntry entry) =>
        ResolveLegacyDirectBinding(
            entry.LegacyDirectBinding,
            entry.NyxUserToken,
            entry.NyxRefreshToken,
            entry.VerificationToken,
            entry.CredentialRef,
            entry.EncryptKey);

    public static ChannelBotLegacyDirectBinding? ResolveLegacyDirectBinding(this ChannelBotRegisterCommand command) =>
        ResolveLegacyDirectBinding(
            command.LegacyDirectBinding,
            command.NyxUserToken,
            command.NyxRefreshToken,
            command.VerificationToken,
            command.CredentialRef,
            command.EncryptKey);

    public static ChannelBotLegacyDirectBinding? ResolveLegacyDirectBinding(this ChannelBotUpdateTokenCommand command) =>
        ResolveLegacyDirectBinding(
            command.LegacyDirectBinding,
            command.NyxUserToken,
            command.NyxRefreshToken,
            string.Empty,
            string.Empty,
            string.Empty);

    public static ChannelBotLegacyDirectBinding? ResolveLegacyDirectBinding(this ChannelBotTokenUpdatedEvent domainEvent) =>
        ResolveLegacyDirectBinding(
            domainEvent.LegacyDirectBinding,
            domainEvent.NyxUserToken,
            domainEvent.NyxRefreshToken,
            string.Empty,
            string.Empty,
            string.Empty);

    public static ChannelBotLegacyDirectBinding? ResolveLegacyDirectBinding(this ChannelBotRegistrationDocument document) =>
        ResolveLegacyDirectBinding(
            binding: null,
            legacyNyxUserToken: document.NyxUserToken,
            legacyNyxRefreshToken: document.NyxRefreshToken,
            legacyVerificationToken: document.VerificationToken,
            legacyCredentialRef: document.CredentialRef,
            legacyEncryptKey: document.EncryptKey);

    public static void ApplyLegacyDirectBinding(this ChannelBotRegistrationEntry entry, ChannelBotLegacyDirectBinding? binding)
    {
        binding = Clone(binding);
        entry.LegacyDirectBinding = binding;
        entry.NyxUserToken = binding?.NyxUserToken ?? string.Empty;
        entry.NyxRefreshToken = binding?.NyxRefreshToken ?? string.Empty;
        entry.VerificationToken = binding?.VerificationToken ?? string.Empty;
        entry.CredentialRef = binding?.CredentialRef ?? string.Empty;
        entry.EncryptKey = binding?.EncryptKey ?? string.Empty;
    }

    public static void ApplyLegacyDirectBinding(this ChannelBotRegisterCommand command, ChannelBotLegacyDirectBinding? binding)
    {
        binding = Clone(binding);
        command.LegacyDirectBinding = binding;
        command.NyxUserToken = binding?.NyxUserToken ?? string.Empty;
        command.NyxRefreshToken = binding?.NyxRefreshToken ?? string.Empty;
        command.VerificationToken = binding?.VerificationToken ?? string.Empty;
        command.CredentialRef = binding?.CredentialRef ?? string.Empty;
        command.EncryptKey = binding?.EncryptKey ?? string.Empty;
    }

    public static void ApplyLegacyDirectBinding(this ChannelBotUpdateTokenCommand command, ChannelBotLegacyDirectBinding? binding)
    {
        binding = Clone(binding);
        command.LegacyDirectBinding = binding;
        command.NyxUserToken = binding?.NyxUserToken ?? string.Empty;
        command.NyxRefreshToken = binding?.NyxRefreshToken ?? string.Empty;
    }

    public static void ApplyLegacyDirectBinding(this ChannelBotTokenUpdatedEvent domainEvent, ChannelBotLegacyDirectBinding? binding)
    {
        binding = Clone(binding);
        domainEvent.LegacyDirectBinding = binding;
        domainEvent.NyxUserToken = binding?.NyxUserToken ?? string.Empty;
        domainEvent.NyxRefreshToken = binding?.NyxRefreshToken ?? string.Empty;
    }

    private static ChannelBotLegacyDirectBinding? ResolveLegacyDirectBinding(
        ChannelBotLegacyDirectBinding? binding,
        string? legacyNyxUserToken,
        string? legacyNyxRefreshToken,
        string? legacyVerificationToken,
        string? legacyCredentialRef,
        string? legacyEncryptKey)
    {
        var userToken = FirstNonEmpty(binding?.NyxUserToken, legacyNyxUserToken);
        var refreshToken = FirstNonEmpty(binding?.NyxRefreshToken, legacyNyxRefreshToken);
        var verificationToken = FirstNonEmpty(binding?.VerificationToken, legacyVerificationToken);
        var credentialRef = FirstNonEmpty(binding?.CredentialRef, legacyCredentialRef);
        var encryptKey = FirstNonEmpty(binding?.EncryptKey, legacyEncryptKey);

        if (string.IsNullOrWhiteSpace(userToken) &&
            string.IsNullOrWhiteSpace(refreshToken) &&
            string.IsNullOrWhiteSpace(verificationToken) &&
            string.IsNullOrWhiteSpace(credentialRef) &&
            string.IsNullOrWhiteSpace(encryptKey))
        {
            return null;
        }

        return new ChannelBotLegacyDirectBinding
        {
            NyxUserToken = userToken,
            NyxRefreshToken = refreshToken,
            VerificationToken = verificationToken,
            CredentialRef = credentialRef,
            EncryptKey = encryptKey,
        };
    }

    private static ChannelBotLegacyDirectBinding? Clone(ChannelBotLegacyDirectBinding? binding) =>
        binding is null
            ? null
            : new ChannelBotLegacyDirectBinding
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
