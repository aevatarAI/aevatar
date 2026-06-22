using Aevatar.GAgentService.Core.Ports;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class ConfiguredNyxIdRegistrationTokenAccessor : INyxIdRegistrationTokenAccessor
{
    private readonly NyxIdRegistrationTokenOptions _options;

    public ConfiguredNyxIdRegistrationTokenAccessor(IOptions<NyxIdRegistrationTokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public Task<NyxIdRegistrationToken?> GetTokenAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var token = _options.OwnerAccessToken?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult<NyxIdRegistrationToken?>(null);

        return Task.FromResult<NyxIdRegistrationToken?>(
            new NyxIdRegistrationToken(token, _options.CredentialKid?.Trim() ?? string.Empty));
    }
}

public sealed class NyxIdRegistrationTokenOptions
{
    public const string SectionName = "GAgentService:ExternalExposure:NyxIdRegistration";

    public string OwnerAccessToken { get; set; } = string.Empty;

    public string CredentialKid { get; set; } = string.Empty;
}
