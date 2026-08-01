using System.Reflection;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
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
    public void AddAevatarAIFeatures_ShouldRegisterVoiceWebSocketAttachExecutor()
    {
        using var envScope = new EnvironmentVariablesScope(new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = null,
        });
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddAevatarAIFeatures(config, options =>
        {
            options.EnableMEAIProviders = false;
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
        provider.GetRequiredService<VoiceWebSocketAttachExecutor>()
            .Should().NotBeNull();
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<VoiceWebSocketAttachOptions>>()
            .Value.ConflictRetryAfterSeconds.Should().Be(1);
    }

    [Fact]
    public void VoiceWebSocketAttachOptionsValidator_ShouldRejectInvalidTimeouts()
    {
        var validator = new VoiceWebSocketAttachOptionsValidator();
        var result = validator.Validate(null, new VoiceWebSocketAttachOptions
        {
            AttachTimeout = TimeSpan.Zero,
            CloseWaitTimeout = TimeSpan.Zero,
            PolicyViolationCloseTimeout = TimeSpan.Zero,
            ConflictRetryAfterSeconds = 0,
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(failure => failure.Contains(nameof(VoiceWebSocketAttachOptions.AttachTimeout)));
        result.Failures.Should().Contain(failure => failure.Contains(nameof(VoiceWebSocketAttachOptions.CloseWaitTimeout)));
        result.Failures.Should().Contain(failure => failure.Contains(nameof(VoiceWebSocketAttachOptions.PolicyViolationCloseTimeout)));
        result.Failures.Should().Contain(failure => failure.Contains(nameof(VoiceWebSocketAttachOptions.ConflictRetryAfterSeconds)));
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
    public void AddAevatarAIFeatures_WhenBrokerOnlyOpenAIVoiceConfigured_ShouldCreateDefaultAlias()
    {
        using var envScope = new EnvironmentVariablesScope(new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = null,
        });

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddLogging();
        AddVoicePresenceTestCredentialResolver(services);

        // The ADR-0033 production shape: no long-lived OpenAI key, no MiniCPM provider, only the
        // NyxID ephemeral realtime broker (enabled via its default service slug). The openai module
        // must still own the "voice_presence" default alias — auto-enable and the module mount both
        // target that name, and a name the factory cannot create leaves the session-lease signal
        // unhandled so /ws/voice loops on 503 voice_capability_not_ready.
        services.AddAevatarAIFeatures(config, options => options.EnableMEAIProviders = false);

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

    [Fact]
    public async Task ConnectVoiceProviderSessionAsync_ShouldReturnBeforeToolDiscoveryAndSessionUpdateComplete()
    {
        using var toolCatalog = new BlockingVoiceToolCatalog();
        var provider = new RecordingRealtimeVoiceProvider();
        var handle = CreateLeaseHandle(toolContext: null);

        await using var session = await InvokeConnectVoiceProviderSessionAsync(
            handle,
            provider,
            new VoiceProviderConfig { ProviderName = "test" },
            new VoiceSessionConfig { SampleRateHz = 24000 },
            toolCatalog);

        provider.Session.Should().NotBeNull();
        provider.Session!.UpdateCalls.Should().Be(0);
        await toolCatalog.WaitForDiscoveryAsync();

        toolCatalog.Release([new VoiceToolDefinition
        {
            Name = "door.open",
            Description = "open door",
            ParametersSchema = """{"type":"object"}""",
        }]);

        await provider.Session.WaitForUpdateAsync();
        provider.Session.UpdateCalls.Should().Be(1);
        provider.Session.LastSession!.ToolDefinitions
            .Should().ContainSingle(definition => definition.Name == "door.open");
    }

    [Fact]
    public async Task ReadinessGatedSession_ShouldPassAudioAndCancelThroughBeforeReadiness()
    {
        using var toolCatalog = new BlockingVoiceToolCatalog();
        var provider = new RecordingRealtimeVoiceProvider();
        var handle = CreateLeaseHandle(toolContext: null);

        await using var session = await InvokeConnectVoiceProviderSessionAsync(
            handle,
            provider,
            new VoiceProviderConfig { ProviderName = "test" },
            new VoiceSessionConfig { SampleRateHz = 24000 },
            toolCatalog);

        await toolCatalog.WaitForDiscoveryAsync();
        await session.SendAudioAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await session.CancelResponseAsync(CancellationToken.None);

        provider.Session!.SentAudioFrames.Should().ContainSingle()
            .Which.Should().Equal([1, 2, 3]);
        provider.Session.CancelCalls.Should().Be(1);
        provider.Session.UpdateCalls.Should().Be(0);

        toolCatalog.Release([]);
        await provider.Session.WaitForUpdateAsync();
    }

    [Fact]
    public async Task ReadinessGatedSession_ShouldGateResponseProducingMethodsOnReadiness()
    {
        using var toolCatalog = new BlockingVoiceToolCatalog();
        var provider = new RecordingRealtimeVoiceProvider();
        var handle = CreateLeaseHandle(toolContext: null);

        await using var session = await InvokeConnectVoiceProviderSessionAsync(
            handle,
            provider,
            new VoiceProviderConfig { ProviderName = "test" },
            new VoiceSessionConfig { SampleRateHz = 24000 },
            toolCatalog);

        await toolCatalog.WaitForDiscoveryAsync();
        var sendToolResult = session.SendToolResultAsync("call-1", """{"ok":true}""", CancellationToken.None);

        provider.Session!.ToolResultCalls.Should().Be(0);
        sendToolResult.IsCompleted.Should().BeFalse();

        toolCatalog.Release([]);
        await provider.Session.WaitForUpdateAsync();
        await sendToolResult.WaitAsync(TimeSpan.FromSeconds(5));

        provider.Session.ToolResultCalls.Should().Be(1);
    }

    [Fact]
    public async Task ConnectVoiceProviderSessionAsync_ShouldPassLeaseToolContextToCatalog()
    {
        var toolCatalog = new CapturingVoiceToolCatalog();
        var provider = new RecordingRealtimeVoiceProvider();
        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = "voice-tool:ref-1",
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow.AddMinutes(5)),
        };
        var handle = CreateLeaseHandle(toolContext);

        await using var session = await InvokeConnectVoiceProviderSessionAsync(
            handle,
            provider,
            new VoiceProviderConfig { ProviderName = "test" },
            new VoiceSessionConfig { SampleRateHz = 24000 },
            toolCatalog);

        await provider.Session!.WaitForUpdateAsync();

        toolCatalog.Contexts.Should().ContainSingle();
        toolCatalog.Contexts[0]!.CredentialRef.Should().Be("voice-tool:ref-1");
        provider.Session.LastSession!.ToolDefinitions
            .Should().ContainSingle(definition => definition.Name == "caller.only");
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

    private static VoicePresenceSessionLeaseHandle CreateLeaseHandle(VoiceToolExecutionContext? toolContext) =>
        new(
            "agent-1",
            "voice_presence",
            "lease-1",
            "host-1",
            8,
            DateTimeOffset.UtcNow.AddMinutes(5),
            VoiceRemoteAudioSupport.Supported,
            "transport-1",
            17,
            toolContext);

    private static void AddVoicePresenceTestCredentialResolver(IServiceCollection services)
    {
        services.AddSingleton<IRealtimeProviderCredentialResolver, NoOpRealtimeProviderCredentialResolver>();
        services.AddSingleton<IAgentToolExecutionPort, UnusedAgentToolExecutionPort>();
    }

    internal static void AddToolExecutionAuditDependencies(IServiceCollection services)
    {
        services.AddSingleton<IAuditTrailAppender, AppendedAuditTrail>();
        services.AddSingleton<IAuditActorIdentityHasher, StableIdentityHasher>();
    }

    internal sealed class UnusedAgentToolExecutionPort : IAgentToolExecutionPort
    {
        public Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Voice bootstrap composition does not execute agent tools.");
    }

    internal sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "test-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

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
        public RecordingRealtimeVoiceProviderSession? Session { get; private set; }

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
            Session = new RecordingRealtimeVoiceProviderSession();
            return Task.FromResult<RealtimeVoiceProviderSession>(Session);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRealtimeVoiceProviderSession : RealtimeVoiceProviderSession
    {
        private readonly TaskCompletionSource _sessionUpdated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> SentAudioFrames { get; } = [];
        public int CancelCalls { get; private set; }
        public int ToolResultCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public VoiceSessionConfig? LastSession { get; private set; }

        public override Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = ct;
            SentAudioFrames.Add(pcm16.ToArray());
            return Task.CompletedTask;
        }

        public override Task SendInputImageAsync(VoiceInputImage inputImage, CancellationToken ct) => Task.CompletedTask;

        public override Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
        {
            _ = callId;
            _ = resultJson;
            _ = ct;
            ToolResultCalls++;
            return Task.CompletedTask;
        }

        public override Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct) =>
            Task.CompletedTask;

        public override Task CancelResponseAsync(CancellationToken ct)
        {
            _ = ct;
            CancelCalls++;
            return Task.CompletedTask;
        }

        public override Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
        {
            _ = ct;
            UpdateCalls++;
            LastSession = session.Clone();
            _sessionUpdated.TrySetResult();
            return Task.CompletedTask;
        }

        public Task WaitForUpdateAsync() =>
            _sessionUpdated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingVoiceToolCatalog : IVoiceToolCatalog, IDisposable
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyList<VoiceToolDefinition>> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<VoiceToolDefinition>> DiscoverAsync(
            VoiceToolExecutionContext? toolContext = null,
            CancellationToken ct = default)
        {
            _ = toolContext;
            _entered.TrySetResult();
            return _release.Task.WaitAsync(ct);
        }

        public Task WaitForDiscoveryAsync() =>
            _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release(IReadOnlyList<VoiceToolDefinition> definitions) =>
            _release.TrySetResult(definitions);

        public void Dispose()
        {
            _release.TrySetResult([]);
        }
    }

    private sealed class CapturingVoiceToolCatalog : IVoiceToolCatalog
    {
        public List<VoiceToolExecutionContext?> Contexts { get; } = [];

        public Task<IReadOnlyList<VoiceToolDefinition>> DiscoverAsync(
            VoiceToolExecutionContext? toolContext = null,
            CancellationToken ct = default)
        {
            _ = ct;
            Contexts.Add(toolContext?.Clone());
            return Task.FromResult<IReadOnlyList<VoiceToolDefinition>>(
            [
                new VoiceToolDefinition
                {
                    Name = "caller.only",
                    Description = "caller",
                    ParametersSchema = "{}",
                },
            ]);
        }
    }
}
