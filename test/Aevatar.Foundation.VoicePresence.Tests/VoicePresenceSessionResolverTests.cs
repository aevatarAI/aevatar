using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceSessionResolverTests
{
    [Fact]
    public async Task ResolveAsync_should_return_voice_session_for_agent_with_single_voice_module()
    {
        var module = CreateModule("voice_presence", 16000);
        var runtime = new StubActorRuntime(new StubActor("agent-1", new TestAgent("agent-1", [module])));
        var dispatchPort = new RecordingDispatchPort();
        using var services = BuildServices(runtime, dispatchPort);
        var resolver = new InProcessActorVoicePresenceSessionResolver(services);

        var session = await resolver.ResolveAsync("agent-1");

        session.ShouldNotBeNull();
        session.Module.ShouldBeSameAs(module);
        session.PcmSampleRateHz.ShouldBe(16000);

        var controlFrame = new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 1,
                PlayoutSequence = 3,
            },
        };
        await session.SelfEventDispatcher(controlFrame, CancellationToken.None);

        dispatchPort.Dispatches.Count.ShouldBe(1);
        dispatchPort.Dispatches[0].ActorId.ShouldBe("agent-1");
        dispatchPort.Dispatches[0].Envelope.Route.GetTopologyAudience().ShouldBe(TopologyAudience.Self);
        dispatchPort.Dispatches[0].Envelope.Payload.ShouldNotBeNull();
        dispatchPort.Dispatches[0].Envelope.Payload.Is(VoiceControlFrame.Descriptor).ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_should_prefer_default_voice_module_when_multiple_are_attached()
    {
        var defaultModule = CreateModule("voice_presence", 24000);
        var alternateModule = CreateModule("voice_presence_openai", 16000);
        var runtime = new StubActorRuntime(new StubActor("agent-1", new TestAgent("agent-1", [alternateModule, defaultModule])));
        using var services = BuildServices(runtime, new RecordingDispatchPort());
        var resolver = new InProcessActorVoicePresenceSessionResolver(services);

        var session = await resolver.ResolveAsync("agent-1");

        session.ShouldNotBeNull();
        session.Module.ShouldBeSameAs(defaultModule);
        session.PcmSampleRateHz.ShouldBe(24000);
    }

    [Fact]
    public async Task ResolveAsync_should_return_null_when_actor_has_no_voice_module()
    {
        var runtime = new StubActorRuntime(new StubActor("agent-1", new TestAgent("agent-1", [])));
        using var services = BuildServices(runtime, new RecordingDispatchPort());
        var resolver = new InProcessActorVoicePresenceSessionResolver(services);

        var session = await resolver.ResolveAsync("agent-1");

        session.ShouldBeNull();
    }

    private static VoicePresenceModule CreateModule(string name, int sampleRateHz) =>
        new(
            new RecordingVoiceProvider(),
            new VoiceProviderConfig
            {
                ProviderName = "openai",
                ApiKey = "sk-test",
                Model = "gpt-realtime",
            },
            new VoiceSessionConfig
            {
                Voice = "alloy",
                SampleRateHz = sampleRateHz,
            },
            new VoicePresenceModuleOptions
            {
                Name = name,
            });

    private static ServiceProvider BuildServices(IActorRuntime runtime, IActorDispatchPort dispatchPort)
    {
        var services = new ServiceCollection();
        services.AddSingleton(runtime);
        services.AddSingleton(dispatchPort);
        return services.BuildServiceProvider();
    }

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

    private sealed class TestAgent(
        string id,
        IReadOnlyList<IEventModule<IEventHandlerContext>> modules)
        : IAgent, IEventModuleContainer<IEventHandlerContext>
    {
        public string Id => id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult($"test-agent:{id}");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IReadOnlyList<IEventModule<IEventHandlerContext>> GetModules() => modules;
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingVoiceProvider : IRealtimeVoiceProvider
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
