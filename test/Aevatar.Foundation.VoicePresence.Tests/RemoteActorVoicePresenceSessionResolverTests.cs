using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
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
    public async Task ResolveAsync_should_bridge_remote_voice_outputs_through_actor_stream()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1")));
        var dispatchPort = new RecordingDispatchPort();
        var subscriptions = new RecordingSubscriptionProvider();
        using var services = BuildServices(runtime, dispatchPort, subscriptions);
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
        await session.AttachTransportAsync(transport, CancellationToken.None);

        dispatchPort.Dispatches.ShouldHaveSingleItem();
        var openSignal = dispatchPort.Dispatches[0].Envelope.Payload!.Unpack<VoiceModuleSignal>();
        openSignal.ModuleName.ShouldBe("voice_presence_openai");
        openSignal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.RemoteSessionOpenRequested);

        await subscriptions.PublishAsync(
            "agent-1",
            new VoiceRemoteTransportOutput
            {
                ModuleName = "voice_presence_openai",
                SessionId = openSignal.RemoteSessionOpenRequested.SessionId,
                AudioOutput = new VoiceAudioReceived
                {
                    Pcm16 = ByteString.CopyFrom([1, 2, 3]),
                    SampleRateHz = 16000,
                },
            });

        transport.AudioFrames.ShouldHaveSingleItem();
        transport.AudioFrames[0].ShouldBe([1, 2, 3]);

        await subscriptions.PublishAsync(
            "agent-1",
            new VoiceRemoteTransportOutput
            {
                ModuleName = "voice_presence_openai",
                SessionId = openSignal.RemoteSessionOpenRequested.SessionId,
                SessionClosed = new VoiceRemoteSessionClosed
                {
                    Reason = "provider_disconnected",
                },
            });

        await transport.DisposedTask.Task;
        transport.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DetachTransportAsync_without_local_attachment_should_issue_best_effort_remote_close()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1")));
        var dispatchPort = new RecordingDispatchPort();
        var subscriptions = new RecordingSubscriptionProvider();
        using var services = BuildServices(runtime, dispatchPort, subscriptions);
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
                   new RecordingDispatchPort(),
                   new RecordingSubscriptionProvider()))
        {
            var resolver = new RemoteActorVoicePresenceSessionResolver(actorMissingServices);
            var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1"));
            session.ShouldBeNull();
        }

        using var services = BuildServices(
            new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1"))),
            new RecordingDispatchPort(),
            new RecordingSubscriptionProvider());
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
            new RecordingDispatchPort(),
            new RecordingSubscriptionProvider());

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
    public async Task AttachTransportAsync_should_relay_input_audio_and_control_frames_and_detach_on_completion()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1")));
        var dispatchPort = new RecordingDispatchPort();
        var subscriptions = new RecordingSubscriptionProvider();
        using var services = BuildServices(runtime, dispatchPort, subscriptions);
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

        await session.AttachTransportAsync(transport, CancellationToken.None);
        await transport.DisposedTask.Task.WaitAsync(TimeSpan.FromSeconds(5));

        dispatchPort.Dispatches.Count.ShouldBeGreaterThanOrEqualTo(3);
        dispatchPort.Dispatches.ShouldContain(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteSessionOpenRequested);
        dispatchPort.Dispatches.ShouldContain(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteAudioInputReceived);
        dispatchPort.Dispatches.ShouldContain(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteControlInputReceived);
        dispatchPort.Dispatches.ShouldContain(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteSessionCloseRequested);
    }

    [Fact]
    public async Task DetachTransportAsync_should_ignore_mismatched_expected_transport()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1")));
        var dispatchPort = new RecordingDispatchPort();
        var subscriptions = new RecordingSubscriptionProvider();
        using var services = BuildServices(runtime, dispatchPort, subscriptions);
        var resolver = new RemoteActorVoicePresenceSessionResolver(services);
        var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1", "voice_presence"));

        session.ShouldNotBeNull();
        var transport = new HoldingVoiceTransport();
        await session.AttachTransportAsync(transport, CancellationToken.None);

        await session.DetachTransportAsync(new StubVoiceTransport(), CancellationToken.None);

        transport.Disposed.ShouldBeFalse();
        session.IsTransportAttached.ShouldBeTrue();

        await session.DetachTransportAsync(transport, CancellationToken.None);
        await transport.DisposedTask.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.IsTransportAttached.ShouldBeFalse();
    }

    [Fact]
    public async Task Remote_output_send_failure_should_release_transport_and_issue_close()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new PlainAgent("agent-1")));
        var dispatchPort = new RecordingDispatchPort();
        var subscriptions = new RecordingSubscriptionProvider();
        using var services = BuildServices(runtime, dispatchPort, subscriptions);
        var resolver = new RemoteActorVoicePresenceSessionResolver(services);
        var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1", "voice_presence"));

        session.ShouldNotBeNull();
        var transport = new ThrowingSendVoiceTransport();
        await session.AttachTransportAsync(transport, CancellationToken.None);

        var openSignal = dispatchPort.Dispatches[0].Envelope.Payload!.Unpack<VoiceModuleSignal>();
        await subscriptions.PublishAsync(
            "agent-1",
            new VoiceRemoteTransportOutput
            {
                ModuleName = "other_module",
                SessionId = openSignal.RemoteSessionOpenRequested.SessionId,
                AudioOutput = new VoiceAudioReceived
                {
                    Pcm16 = ByteString.CopyFrom([9]),
                    SampleRateHz = 16000,
                },
            });
        transport.Disposed.ShouldBeFalse();

        await subscriptions.PublishAsync(
            "agent-1",
            new VoiceRemoteTransportOutput
            {
                ModuleName = "voice_presence",
                SessionId = openSignal.RemoteSessionOpenRequested.SessionId,
                AudioOutput = new VoiceAudioReceived
                {
                    Pcm16 = ByteString.CopyFrom([9]),
                    SampleRateHz = 16000,
                },
            });

        await transport.DisposedTask.Task.WaitAsync(TimeSpan.FromSeconds(5));
        dispatchPort.Dispatches.ShouldContain(dispatch =>
            dispatch.Envelope.Payload!.Unpack<VoiceModuleSignal>().SignalCase ==
            VoiceModuleSignal.SignalOneofCase.RemoteSessionCloseRequested);
    }

    private static ServiceProvider BuildServices(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IActorEventSubscriptionProvider subscriptions)
    {
        var services = new ServiceCollection();
        services.AddSingleton(runtime);
        services.AddSingleton(dispatchPort);
        services.AddSingleton(subscriptions);
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

    private sealed class RecordingSubscriptionProvider : IActorEventSubscriptionProvider
    {
        private readonly Dictionary<string, List<Func<VoiceRemoteTransportOutput, Task>>> _handlers = new(StringComparer.Ordinal);

        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            ct.ThrowIfCancellationRequested();
            if (typeof(TMessage) != typeof(VoiceRemoteTransportOutput))
                throw new NotSupportedException(typeof(TMessage).Name);

            if (!_handlers.TryGetValue(actorId, out var handlers))
            {
                handlers = [];
                _handlers[actorId] = handlers;
            }

            var typedHandler = (Func<VoiceRemoteTransportOutput, Task>)(object)handler;
            handlers.Add(typedHandler);

            return Task.FromResult<IAsyncDisposable>(new SubscriptionLease(() => handlers.Remove(typedHandler)));
        }

        public async Task PublishAsync(string actorId, VoiceRemoteTransportOutput output)
        {
            if (!_handlers.TryGetValue(actorId, out var handlers))
                return;

            foreach (var handler in handlers.ToArray())
                await handler(output);
        }

        private sealed class SubscriptionLease(Action release) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                release();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class HoldingVoiceTransport : IVoiceTransport
    {
        private readonly Channel<VoiceTransportFrame> _frames = Channel.CreateUnbounded<VoiceTransportFrame>();

        public bool Disposed { get; private set; }

        public TaskCompletionSource DisposedTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> AudioFrames { get; } = [];

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = ct;
            AudioFrames.Add(pcm16.ToArray());
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
            DisposedTask.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedVoiceTransport(IEnumerable<VoiceTransportFrame> frames) : IVoiceTransport
    {
        private readonly VoiceTransportFrame[] _frames = frames.ToArray();

        public bool Disposed { get; private set; }

        public TaskCompletionSource DisposedTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            DisposedTask.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubVoiceTransport : IVoiceTransport
    {
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
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingSendVoiceTransport : IVoiceTransport
    {
        private readonly Channel<VoiceTransportFrame> _frames = Channel.CreateUnbounded<VoiceTransportFrame>();

        public bool Disposed { get; private set; }

        public TaskCompletionSource DisposedTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = pcm16;
            _ = ct;
            throw new InvalidOperationException("boom");
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
            DisposedTask.TrySetResult();
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
