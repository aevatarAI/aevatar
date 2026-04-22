namespace Aevatar.GAgents.ChannelRuntime;

public sealed partial class ChannelBotRegistrationEntry
{
    public string NyxUserToken
    {
        get => LegacyDirectBinding?.NyxUserToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().NyxUserToken = value ?? string.Empty;
    }

    public string NyxRefreshToken
    {
        get => LegacyDirectBinding?.NyxRefreshToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().NyxRefreshToken = value ?? string.Empty;
    }

    public string VerificationToken
    {
        get => LegacyDirectBinding?.VerificationToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().VerificationToken = value ?? string.Empty;
    }

    public string CredentialRef
    {
        get => LegacyDirectBinding?.CredentialRef ?? string.Empty;
        set => EnsureLegacyDirectBinding().CredentialRef = value ?? string.Empty;
    }

    public string EncryptKey
    {
        get => LegacyDirectBinding?.EncryptKey ?? string.Empty;
        set => EnsureLegacyDirectBinding().EncryptKey = value ?? string.Empty;
    }

    private ChannelBotLegacyDirectBinding EnsureLegacyDirectBinding()
    {
        LegacyDirectBinding ??= new ChannelBotLegacyDirectBinding();
        return LegacyDirectBinding;
    }
}

public sealed partial class ChannelBotRegisterCommand
{
    public string NyxUserToken
    {
        get => LegacyDirectBinding?.NyxUserToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().NyxUserToken = value ?? string.Empty;
    }

    public string NyxRefreshToken
    {
        get => LegacyDirectBinding?.NyxRefreshToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().NyxRefreshToken = value ?? string.Empty;
    }

    public string VerificationToken
    {
        get => LegacyDirectBinding?.VerificationToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().VerificationToken = value ?? string.Empty;
    }

    public string CredentialRef
    {
        get => LegacyDirectBinding?.CredentialRef ?? string.Empty;
        set => EnsureLegacyDirectBinding().CredentialRef = value ?? string.Empty;
    }

    public string EncryptKey
    {
        get => LegacyDirectBinding?.EncryptKey ?? string.Empty;
        set => EnsureLegacyDirectBinding().EncryptKey = value ?? string.Empty;
    }

    private ChannelBotLegacyDirectBinding EnsureLegacyDirectBinding()
    {
        LegacyDirectBinding ??= new ChannelBotLegacyDirectBinding();
        return LegacyDirectBinding;
    }
}

public sealed partial class ChannelBotUpdateTokenCommand
{
    public string NyxUserToken
    {
        get => LegacyDirectBinding?.NyxUserToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().NyxUserToken = value ?? string.Empty;
    }

    public string NyxRefreshToken
    {
        get => LegacyDirectBinding?.NyxRefreshToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().NyxRefreshToken = value ?? string.Empty;
    }

    private ChannelBotLegacyDirectBinding EnsureLegacyDirectBinding()
    {
        LegacyDirectBinding ??= new ChannelBotLegacyDirectBinding();
        return LegacyDirectBinding;
    }
}

public sealed partial class ChannelBotTokenUpdatedEvent
{
    public string NyxUserToken
    {
        get => LegacyDirectBinding?.NyxUserToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().NyxUserToken = value ?? string.Empty;
    }

    public string NyxRefreshToken
    {
        get => LegacyDirectBinding?.NyxRefreshToken ?? string.Empty;
        set => EnsureLegacyDirectBinding().NyxRefreshToken = value ?? string.Empty;
    }

    private ChannelBotLegacyDirectBinding EnsureLegacyDirectBinding()
    {
        LegacyDirectBinding ??= new ChannelBotLegacyDirectBinding();
        return LegacyDirectBinding;
    }
}
