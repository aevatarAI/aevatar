using System.Text.Json;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class LarkConversationAdapterFactory
{
    private readonly LarkMessageComposer _composer;
    private readonly LarkPayloadRedactor _payloadRedactor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICredentialProvider? _credentialProvider;

    public LarkConversationAdapterFactory(
        LarkMessageComposer composer,
        LarkPayloadRedactor payloadRedactor,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ICredentialProvider? credentialProvider = null)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _payloadRedactor = payloadRedactor ?? throw new ArgumentNullException(nameof(payloadRedactor));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _credentialProvider = credentialProvider;
    }

    public async Task<LarkChannelAdapter> CreateAsync(ChannelBotRegistrationEntry registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ct.ThrowIfCancellationRequested();

        var snapshot = await AdapterSnapshot.FromAsync(registration, _credentialProvider, ct);
        var adapter = new LarkChannelAdapter(
            new StaticCredentialProvider(snapshot.SecretJson),
            _composer,
            _payloadRedactor,
            _loggerFactory.CreateLogger<LarkChannelAdapter>(),
            _httpClientFactory.CreateClient(LarkConversationHostDefaults.HttpClientName),
            captureInboundActivities: false);

        await adapter.InitializeAsync(
            ChannelTransportBinding.Create(
                ChannelBotDescriptor.Create(
                    snapshot.RegistrationId,
                    ChannelId.From("lark"),
                    BotInstanceId.From(snapshot.RegistrationId),
                    snapshot.ScopeId),
                credentialRef: snapshot.BindingCredentialRef,
                verificationToken: snapshot.VerificationToken),
            ct);
        await adapter.StartReceivingAsync(ct);
        return adapter;
    }

    private sealed record AdapterSnapshot(
        string RegistrationId,
        string ScopeId,
        string VerificationToken,
        string BindingCredentialRef,
        string SecretJson)
    {
        public static async Task<AdapterSnapshot> FromAsync(
            ChannelBotRegistrationEntry registration,
            ICredentialProvider? credentialProvider,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ct.ThrowIfCancellationRequested();

            var credentialRef = registration.GetCredentialRef().Trim();
            var fallbackEncryptKey = registration.GetEncryptKey().Trim();
            var bindingCredentialRef = credentialRef.Length == 0
                ? $"channel-registration:lark:{registration.Id ?? string.Empty}"
                : credentialRef;

            var secretJson = credentialRef.Length == 0
                ? BuildSecretJson(fallbackEncryptKey)
                : BuildSecretJson(await ResolveSecretAsync(credentialRef, credentialProvider, ct));

            return new AdapterSnapshot(
                registration.Id ?? string.Empty,
                registration.ScopeId ?? string.Empty,
                registration.GetVerificationToken(),
                bindingCredentialRef,
                secretJson);
        }

        private static async Task<string> ResolveSecretAsync(
            string credentialRef,
            ICredentialProvider? credentialProvider,
            CancellationToken ct)
        {
            if (credentialProvider is null)
            {
                throw new InvalidOperationException(
                    $"No {nameof(ICredentialProvider)} is registered, but channel registration requires credential_ref '{credentialRef}'.");
            }

            var secret = await credentialProvider.ResolveAsync(credentialRef, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException(
                    $"credential_ref '{credentialRef}' did not resolve to a usable Lark secret.");
            }

            return secret.Trim();
        }

        private static string BuildSecretJson(string resolvedSecret)
        {
            if (string.IsNullOrWhiteSpace(resolvedSecret))
            {
                return JsonSerializer.Serialize(new
                {
                    access_token = string.Empty,
                    encrypt_key = string.Empty,
                });
            }

            var trimmed = resolvedSecret.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
                return trimmed;

            return JsonSerializer.Serialize(new
            {
                access_token = string.Empty,
                encrypt_key = trimmed,
            });
        }
    }

    private sealed class StaticCredentialProvider : ICredentialProvider
    {
        private readonly string _secret;

        public StaticCredentialProvider(string secret)
        {
            _secret = secret ?? string.Empty;
        }

        public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(_secret);
        }
    }
}
