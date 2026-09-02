using Aevatar.Authentication.ScopeServiceTokens;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class ConfiguredNyxIdRegistrationTokenAccessor : INyxIdRegistrationTokenAccessor
{
    private readonly NyxIdRegistrationTokenOptions _options;
    private readonly IScopeServiceTokenIssuer? _scopeTokenIssuer;

    public ConfiguredNyxIdRegistrationTokenAccessor(
        IOptions<NyxIdRegistrationTokenOptions> options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _options = options.Value;
        _scopeTokenIssuer = serviceProvider.GetService<IScopeServiceTokenIssuer>();
    }

    public Task<NyxIdRegistrationToken?> GetTokenAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ct.ThrowIfCancellationRequested();
        var token = _options.OwnerAccessToken?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult<NyxIdRegistrationToken?>(null);

        var credential = _scopeTokenIssuer?.Issue(new ScopeServiceTokenRequest(
            ScopeId: identity.TenantId,
            ServiceId: identity.ServiceId,
            ServiceKey: ServiceKeys.Build(identity),
            TenantId: identity.TenantId,
            AppId: identity.AppId,
            Namespace: identity.Namespace));

        return Task.FromResult<NyxIdRegistrationToken?>(
            new NyxIdRegistrationToken(
                token,
                credential?.AccessToken ?? string.Empty,
                credential?.Kid ?? _options.CredentialKid?.Trim() ?? string.Empty));
    }
}

public sealed class NyxIdRegistrationTokenOptions
{
    public const string SectionName = "GAgentService:ExternalExposure:NyxIdRegistration";

    public string OwnerAccessToken { get; set; } = string.Empty;

    public string CredentialKid { get; set; } = string.Empty;
}
