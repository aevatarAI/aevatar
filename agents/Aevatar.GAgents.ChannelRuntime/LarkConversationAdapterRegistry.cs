using System.Collections.Concurrent;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Lark;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class LarkConversationAdapterRegistry : IAsyncDisposable
{
    private readonly LarkMessageComposer _composer;
    private readonly LarkPayloadRedactor _payloadRedactor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, AdapterRegistration> _registrations = new(StringComparer.Ordinal);

    public LarkConversationAdapterRegistry(
        LarkMessageComposer composer,
        LarkPayloadRedactor payloadRedactor,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IServiceProvider services)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _payloadRedactor = payloadRedactor ?? throw new ArgumentNullException(nameof(payloadRedactor));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public async Task<LarkChannelAdapter> GetAsync(ChannelBotRegistrationEntry registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ct.ThrowIfCancellationRequested();

        var snapshot = await AdapterSnapshot.FromAsync(registration, _services.GetService<ICredentialProvider>(), ct);
        if (_registrations.TryGetValue(snapshot.RegistrationId, out var existing) &&
            existing.Snapshot == snapshot)
        {
            return existing.Adapter;
        }

        var adapter = await CreateAsync(snapshot, ct);
        var replacement = new AdapterRegistration(snapshot, adapter);
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_registrations.TryAdd(snapshot.RegistrationId, replacement))
                return adapter;

            if (!_registrations.TryGetValue(snapshot.RegistrationId, out var observed))
                continue;

            if (observed.Snapshot == snapshot)
            {
                await adapter.StopReceivingAsync(CancellationToken.None);
                return observed.Adapter;
            }

            if (_registrations.TryUpdate(snapshot.RegistrationId, replacement, observed))
            {
                await observed.Adapter.StopReceivingAsync(CancellationToken.None);
                return adapter;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var registration in _registrations.Values)
        {
            try
            {
                await registration.Adapter.StopReceivingAsync(CancellationToken.None);
            }
            catch
            {
            }
        }

        _registrations.Clear();
    }

    private async Task<LarkChannelAdapter> CreateAsync(AdapterSnapshot snapshot, CancellationToken ct)
    {
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

    private sealed record AdapterRegistration(
        AdapterSnapshot Snapshot,
        LarkChannelAdapter Adapter);

    private sealed record AdapterSnapshot(
        string RegistrationId,
        string ScopeId,
        string VerificationToken,
        string BindingCredentialRef,
        string CredentialRef,
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
            var legacyEncryptKey = registration.GetEncryptKey().Trim();
            var bindingCredentialRef = credentialRef.Length == 0
                ? $"channel-registration:lark:{registration.Id ?? string.Empty}"
                : credentialRef;

            var secretJson = credentialRef.Length == 0
                ? BuildSecretJson(legacyEncryptKey)
                : BuildSecretJson(await ResolveSecretAsync(credentialRef, credentialProvider, ct));

            return new AdapterSnapshot(
                registration.Id ?? string.Empty,
                registration.ScopeId ?? string.Empty,
                registration.GetVerificationToken(),
                bindingCredentialRef,
                credentialRef,
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
