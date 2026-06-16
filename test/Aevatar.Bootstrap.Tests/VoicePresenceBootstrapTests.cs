using System.Reflection;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Modules;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Tests;

[Collection(ProcessEnvSerialCollection.Name)]
public sealed class VoicePresenceBootstrapTests
{
    [Fact]
    public void AddAevatarAIFeatures_ShouldRegisterLeaseObservationPort()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options => options.EnableMEAIProviders = false);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IVoicePresenceLeaseObservationPort>()
            .Should().BeOfType<VoicePresenceLeaseObservationPort>();
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenVoicePresenceOpenAIEndpointOnlyConfigured_ShouldRegisterOpenAIThroughBroker()
    {
        using var envScope = new EnvironmentVariablesScope(new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = null,
        });

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddLogging();
        AddVoicePresenceTestCredentialResolver(services);

        services.AddAevatarAIFeatures(config, options =>
        {
            options.EnableMEAIProviders = false;
            options.VoicePresence.DefaultProvider = "minicpm";
            options.VoicePresence.OpenAIProvider = new VoiceProviderConfig
            {
                ProviderName = "openai",
                Endpoint = "https://nyx.example.com/api/v1/proxy/s/llm-openai",
            };
            options.VoicePresence.OpenAISession = new VoiceSessionConfig
            {
                Voice = "alloy",
                SampleRateHz = 24000,
            };
            options.VoicePresence.MiniCPMProvider = new VoiceProviderConfig
            {
                ProviderName = "minicpm",
                Endpoint = "https://minicpm.example.com",
            };
            options.VoicePresence.MiniCPMSession = new VoiceSessionConfig
            {
                SampleRateHz = 16000,
            };
        });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetServices<IEventModuleFactory<IEventHandlerContext>>()
            .OfType<VoicePresenceModuleFactory>()
            .Single();

        factory.TryCreate("voice_presence", out var defaultModule).Should().BeTrue();
        defaultModule.Should().BeOfType<VoicePresenceModule>();

        factory.TryCreate("voice_presence_openai", out var openAIModule).Should().BeTrue();
        openAIModule.Should().BeOfType<VoicePresenceModule>();
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenVoicePresenceMiniCpmConfiguredAsDefault_ShouldCreateDefaultAlias()
    {
        using var envScope = new EnvironmentVariablesScope(new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = null,
        });

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddLogging();
        AddVoicePresenceTestCredentialResolver(services);

        services.AddAevatarAIFeatures(config, options =>
        {
            options.EnableMEAIProviders = false;
            options.VoicePresence.DefaultProvider = "minicpm-o";
            options.VoicePresence.MiniCPMProvider = new VoiceProviderConfig
            {
                ProviderName = "minicpm-o",
                Endpoint = "https://minicpm.example.com",
            };
            options.VoicePresence.MiniCPMSession = new VoiceSessionConfig
            {
                SampleRateHz = 16000,
            };
        });

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetServices<IEventModuleFactory<IEventHandlerContext>>()
            .OfType<VoicePresenceModuleFactory>()
            .Single();

        factory.TryCreate("voice_presence", out var defaultModule).Should().BeTrue();
        defaultModule.Should().BeOfType<VoicePresenceModule>();

        factory.TryCreate("voice_presence_minicpm_o", out var miniCpmModule).Should().BeTrue();
        miniCpmModule.Should().BeOfType<VoicePresenceModule>();

        factory.TryCreate("voice_presence_openai", out var openAIModule).Should().BeTrue();
        openAIModule.Should().BeOfType<VoicePresenceModule>();
    }

    [Fact]
    public async Task ConnectVoiceProviderSessionAsync_ShouldUseHandleLeaseEpochInProviderSessionKey()
    {
        var provider = new RecordingRealtimeVoiceProvider();
        var handle = new VoicePresenceSessionLeaseHandle(
            "agent-1",
            "voice_presence",
            "lease-1",
            "host-1",
            8,
            DateTimeOffset.UtcNow.AddMinutes(5),
            VoiceRemoteAudioSupport.Supported,
            "transport-1",
            17);

        await using var session = await InvokeConnectVoiceProviderSessionAsync(
            handle,
            provider,
            new VoiceProviderConfig { ProviderName = "test" },
            new VoiceSessionConfig { SampleRateHz = 24000 },
            toolCatalog: null);

        provider.SessionKey.Should().NotBeNull();
        provider.SessionKey!.LeaseEpoch.Should().Be(17);
        provider.SessionKey.TransportLeaseId.Should().Be("transport-1");
    }

    private static async Task<RealtimeVoiceProviderSession> InvokeConnectVoiceProviderSessionAsync(
        VoicePresenceSessionLeaseHandle handle,
        IRealtimeVoiceProvider provider,
        VoiceProviderConfig providerConfig,
        VoiceSessionConfig sessionConfig,
        IVoiceToolCatalog? toolCatalog)
    {
        var method = typeof(global::Aevatar.Bootstrap.Extensions.AI.ServiceCollectionExtensions).GetMethod(
            "ConnectVoiceProviderSessionAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var task = method!.Invoke(null,
        [
            handle,
            provider,
            providerConfig,
            sessionConfig,
            toolCatalog,
            new Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task>(
                static (_, _, _) => Task.CompletedTask),
            new Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task>(
                static (_, _, _) => Task.CompletedTask),
            CancellationToken.None,
        ]).Should().BeAssignableTo<Task<RealtimeVoiceProviderSession>>().Subject;

        return await task;
    }

    private static void AddVoicePresenceTestCredentialResolver(IServiceCollection services) =>
        services.AddSingleton<IRealtimeProviderCredentialResolver, NoOpRealtimeProviderCredentialResolver>();

    private sealed class EnvironmentVariablesScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previousValues = new(StringComparer.Ordinal);

        public EnvironmentVariablesScope(IReadOnlyDictionary<string, string?> overrides)
        {
            foreach (var pair in overrides)
            {
                _previousValues[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _previousValues)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private sealed class NoOpRealtimeProviderCredentialResolver : IRealtimeProviderCredentialResolver
    {
        public Task<string?> ResolveApiKeyAsync(
            VoiceProviderSessionKey sessionKey,
            VoiceProviderConfig config,
            CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class RecordingRealtimeVoiceProvider : IRealtimeVoiceProvider
    {
        public VoiceProviderSessionKey? SessionKey { get; private set; }

        public Task<RealtimeVoiceProviderSession> ConnectAsync(
            VoiceProviderSessionKey sessionKey,
            VoiceProviderConfig config,
            Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
            Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
            CancellationToken ct)
        {
            _ = config;
            _ = eventSink;
            _ = audioSink;
            _ = ct;
            SessionKey = sessionKey;
            return Task.FromResult<RealtimeVoiceProviderSession>(new RecordingRealtimeVoiceProviderSession());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRealtimeVoiceProviderSession : RealtimeVoiceProviderSession
    {
        public override Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) => Task.CompletedTask;

        public override Task SendInputImageAsync(VoiceInputImage inputImage, CancellationToken ct) => Task.CompletedTask;

        public override Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct) =>
            Task.CompletedTask;

        public override Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct) =>
            Task.CompletedTask;

        public override Task CancelResponseAsync(CancellationToken ct) => Task.CompletedTask;

        public override Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct) => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
