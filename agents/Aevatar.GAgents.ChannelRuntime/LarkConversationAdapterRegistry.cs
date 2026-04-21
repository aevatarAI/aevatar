using System.Collections.Concurrent;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Lark;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class LarkConversationAdapterRegistry : IAsyncDisposable
{
    private readonly LarkMessageComposer _composer;
    private readonly LarkPayloadRedactor _payloadRedactor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, AdapterRegistration> _registrations = new(StringComparer.Ordinal);

    public LarkConversationAdapterRegistry(
        LarkMessageComposer composer,
        LarkPayloadRedactor payloadRedactor,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _payloadRedactor = payloadRedactor ?? throw new ArgumentNullException(nameof(payloadRedactor));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public async Task<LarkChannelAdapter> GetAsync(ChannelBotRegistrationEntry registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ct.ThrowIfCancellationRequested();

        var snapshot = AdapterSnapshot.From(registration);
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
                credentialRef: $"channel-registration:lark:{snapshot.RegistrationId}",
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
        string SecretJson)
    {
        public static AdapterSnapshot From(ChannelBotRegistrationEntry registration) =>
            new(
                registration.Id ?? string.Empty,
                registration.ScopeId ?? string.Empty,
                registration.VerificationToken ?? string.Empty,
                JsonSerializer.Serialize(new
                {
                    access_token = string.Empty,
                    encrypt_key = registration.EncryptKey ?? string.Empty,
                }));
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
