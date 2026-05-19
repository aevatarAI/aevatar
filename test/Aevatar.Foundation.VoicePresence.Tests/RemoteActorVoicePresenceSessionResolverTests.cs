using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class RemoteActorVoicePresenceSessionResolverTests
{
    [Fact]
    public async Task AttachTransportAsync_should_dispatch_open_then_close_and_fail_remote_audio_transport()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1")));
        var dispatchPort = new RecordingDispatchPort();
        using var services = BuildServices(runtime, dispatchPort);
        var resolver = new RemoteActorVoicePresenceSessionResolver(
            services,
        [
            new VoicePresenceModuleRegistration(
                ["voice_presence_openai"],
                _ => CreateModule("voice_presence_openai"),
                pcmSampleRateHz: 16000),
        ]);

        var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));

        session.ShouldNotBeNull();
        session.PcmSampleRateHz.ShouldBe(16000);

        var transport = new HoldingVoiceTransport();
        var ex = await Should.ThrowAsync<NotSupportedException>(
            () => session.AttachTransportAsync(transport, CancellationToken.None));

        ex.Message.ShouldBe(VoiceRemoteAudioTransportUnavailableException.Reason);
        transport.Disposed.ShouldBeTrue();
        session.IsTransportAttached.ShouldBeFalse();

        dispatchPort.Dispatches.Count.ShouldBe(2);
        var openSignal = dispatchPort.Dispatches[0].Envelope.Payload!.Unpack<VoiceModuleSignal>();
        openSignal.ModuleName.ShouldBe("voice_presence_openai");
        openSignal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.RemoteSessionOpenRequested);

        var closeSignal = dispatchPort.Dispatches[1].Envelope.Payload!.Unpack<VoiceModuleSignal>();
        closeSignal.ModuleName.ShouldBe("voice_presence_openai");
        closeSignal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.RemoteSessionCloseRequested);
        closeSignal.RemoteSessionCloseRequested.SessionId.ShouldBe(openSignal.RemoteSessionOpenRequested.SessionId);
        closeSignal.RemoteSessionCloseRequested.Reason.ShouldBe(VoiceRemoteAudioTransportUnavailableException.Reason);
    }

    [Fact]
    public async Task DetachTransportAsync_without_local_attachment_should_issue_best_effort_remote_close()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1")));
        var dispatchPort = new RecordingDispatchPort();
        using var services = BuildServices(runtime, dispatchPort);
        var resolver = new RemoteActorVoicePresenceSessionResolver(services);

        var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1", "voice_presence"));

        session.ShouldNotBeNull();

        await session.DetachTransportAsync(ct: CancellationToken.None);

        dispatchPort.Dispatches.ShouldHaveSingleItem();
        var closeSignal = dispatchPort.Dispatches[0].Envelope.Payload!.Unpack<VoiceModuleSignal>();
        closeSignal.ModuleName.ShouldBe("voice_presence");
        closeSignal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.RemoteSessionCloseRequested);
        closeSignal.RemoteSessionCloseRequested.SessionId.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_should_return_null_when_services_actor_or_module_are_unavailable()
    {
        using (var missingServices = new ServiceCollection().BuildServiceProvider())
        {
            var resolver = new RemoteActorVoicePresenceSessionResolver(missingServices);
            var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));
            session.ShouldBeNull();
        }

        using (var actorMissingServices = BuildServices(
                   new StubActorRuntime(actor: null),
                   new RecordingDispatchPort()))
        {
            var resolver = new RemoteActorVoicePresenceSessionResolver(actorMissingServices);
            var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));
            session.ShouldBeNull();
        }

        using var services = BuildServices(
            new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1"))),
            new RecordingDispatchPort());
        var unknownModuleResolver = new RemoteActorVoicePresenceSessionResolver(
            services,
        [
            new VoicePresenceModuleRegistration(
                ["voice_presence_openai"],
                _ => CreateModule("voice_presence_openai"),
                pcmSampleRateHz: 16000),
        ]);

        var sessionWithUnknownModule = await unknownModuleResolver.ResolveAsync(
            new VoicePresenceSessionRequest("agent-1", "voice_presence_minicpm"));

        sessionWithUnknownModule.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_should_select_requested_default_and_single_registered_modules()
    {
        using var services = BuildServices(
            new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1"))),
            new RecordingDispatchPort());

        var noRegistrationResolver = new RemoteActorVoicePresenceSessionResolver(services);
        var explicitSession = await noRegistrationResolver.ResolveAsync(
            new VoicePresenceSessionRequest("agent-1", "voice_presence_minicpm"));
        explicitSession.ShouldNotBeNull();
        explicitSession.PcmSampleRateHz.ShouldBe(24000);

        var singleRegistrationResolver = new RemoteActorVoicePresenceSessionResolver(
            services,
        [
            new VoicePresenceModuleRegistration(
                ["voice_presence_openai", "voice_presence"],
                _ => CreateModule("voice_presence_openai"),
                pcmSampleRateHz: 16000),
        ]);
        var defaultedSession = await singleRegistrationResolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));
        defaultedSession.ShouldNotBeNull();
        defaultedSession.PcmSampleRateHz.ShouldBe(16000);
    }

    [Fact]
    public async Task AttachTransportAsync_should_never_dispatch_remote_audio_input_for_transport_frames()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1")));
        var dispatchPort = new RecordingDispatchPort();
        using var services = BuildServices(runtime, dispatchPort);
        var resolver = new RemoteActorVoicePresenceSessionResolver(services);
        var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1", "voice_presence"));

        session.ShouldNotBeNull();
        var transport = new ScriptedVoiceTransport(
        [
            VoiceTransportFrame.Audio(new byte[] { 1, 2, 3 }),
            VoiceTransportFrame.Audio(ReadOnlyMemory<byte>.Empty),
            VoiceTransportFrame.ControlFrame(new VoiceControlFrame
            {
                DrainAcknowledged = new VoiceDrainAcknowledged
                {
                    ResponseId = 3,
                    PlayoutSequence = 4,
                },
            }),
        ]);

        await Should.ThrowAsync<NotSupportedException>(
            () => session.AttachTransportAsync(transport, CancellationToken.None));

        dispatchPort.Dispatches.ShouldContain(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteSessionOpenRequested);
        dispatchPort.Dispatches.ShouldNotContain(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteAudioInputReceived);
        dispatchPort.Dispatches.ShouldContain(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteSessionCloseRequested);
        transport.ReceiveStarted.ShouldBeFalse();
        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task AttachTransportAsync_should_allow_repeated_unsupported_attempts_without_host_attachment_state()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1")));
        var dispatchPort = new RecordingDispatchPort();
        using var services = BuildServices(runtime, dispatchPort);
        var resolver = new RemoteActorVoicePresenceSessionResolver(services);
        var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1", "voice_presence"));

        session.ShouldNotBeNull();
        await Should.ThrowAsync<NotSupportedException>(
            () => session.AttachTransportAsync(new HoldingVoiceTransport(), CancellationToken.None));
        await Should.ThrowAsync<NotSupportedException>(
            () => session.AttachTransportAsync(new HoldingVoiceTransport(), CancellationToken.None));

        dispatchPort.Dispatches.Count(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteSessionOpenRequested).ShouldBe(2);
        dispatchPort.Dispatches.Count(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteSessionCloseRequested).ShouldBe(2);
        session.IsTransportAttached.ShouldBeFalse();
    }

    [Fact]
    public void BuildDirectEnvelope_should_reject_remote_audio_input_payloads()
    {
        Should.Throw<InvalidOperationException>(() =>
            VoicePresenceSessionDispatch.BuildDirectEnvelope(
                "agent-1",
                "voice_presence",
                new VoiceRemoteAudioInputReceived
                {
                    SessionId = "remote-1",
                    Pcm16 = ByteString.CopyFrom([1, 2]),
                }));
    }

    [Fact]
    public void BuildDirectEnvelope_should_keep_remote_control_payloads()
    {
        var envelope = VoicePresenceSessionDispatch.BuildDirectEnvelope(
            "agent-1",
            "voice_presence",
            new VoiceRemoteControlInputReceived
            {
                SessionId = "remote-1",
                ControlFrame = new VoiceControlFrame
                {
                    DrainAcknowledged = new VoiceDrainAcknowledged
                    {
                        ResponseId = 1,
                        PlayoutSequence = 2,
                    },
                },
            });

        var signal = envelope.Payload!.Unpack<VoiceModuleSignal>();
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.RemoteControlInputReceived);
        signal.RemoteControlInputReceived.SessionId.ShouldBe("remote-1");
    }

    private static ServiceProvider BuildServices(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        var services = new ServiceCollection();
        services.AddSingleton(runtime);
        services.AddSingleton(dispatchPort);
        return services.BuildServiceProvider();
    }

    private static VoicePresenceModule CreateModule(string name) =>
        new(
            new NoopVoiceProvider(),
            new VoiceProviderConfig { ProviderName = "openai", ApiKey = "test-key" },
            new VoiceSessionConfig { SampleRateHz = 16000 },
            new VoicePresenceModuleOptions { Name = name });

    private sealed class StubActorRuntime(IActor? actor) : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(actor is { Id: var actorId } && string.Equals(actorId, id, StringComparison.Ordinal)
                ? actor
                : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(actor is { Id: var actorId } && string.Equals(actorId, id, StringComparison.Ordinal));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubActor(string id, IAgent agent) : IActor
    {
        public string Id => id;

        public IAgent Agent => agent;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class PlainAgent(string id) : IAgent
    {
        public string Id => id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(id);

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope.Clone()));
            return Task.CompletedTask;
        }
    }

    private sealed class HoldingVoiceTransport : IVoiceTransport
    {
        private readonly Channel<VoiceTransportFrame> _frames = Channel.CreateUnbounded<VoiceTransportFrame>();

        public bool Disposed { get; private set; }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = ct;
            _ = pcm16;
            return Task.CompletedTask;
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            _ = frame;
            _ = ct;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            while (await _frames.Reader.WaitToReadAsync(ct))
            {
                while (_frames.Reader.TryRead(out var frame))
                    yield return frame;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedVoiceTransport(IEnumerable<VoiceTransportFrame> frames) : IVoiceTransport
    {
        private readonly VoiceTransportFrame[] _frames = frames.ToArray();

        public bool Disposed { get; private set; }

        public bool ReceiveStarted { get; private set; }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = pcm16;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            _ = frame;
            _ = ct;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            ReceiveStarted = true;
            foreach (var frame in _frames)
            {
                ct.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopVoiceProvider : IRealtimeVoiceProvider
    {
        public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent { private get; set; }

        public Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct) => Task.CompletedTask;

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) => Task.CompletedTask;

        public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct) => Task.CompletedTask;

        public Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct) => Task.CompletedTask;

        public Task CancelResponseAsync(CancellationToken ct) => Task.CompletedTask;

        public Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
