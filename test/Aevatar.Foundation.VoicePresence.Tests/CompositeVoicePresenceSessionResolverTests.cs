using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class CompositeVoicePresenceSessionResolverTests
{
    [Fact]
    public async Task ResolveAsync_should_prefer_in_process_session_when_actor_exposes_voice_module()
    {
        var module = CreateModule("voice_presence_openai");
        using var services = BuildServices(new StubActorRuntime(
            new StubActor("agent-1", new ModuleAwareAgent("agent-1", [module]))));
        var resolver = CreateResolver(services);

        var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1", "voice_presence_openai"));

        session.ShouldNotBeNull();
        session.Module.ShouldBeSameAs(module);
        session.PcmSampleRateHz.ShouldBe(16000);
    }

    [Fact]
    public async Task ResolveAsync_should_fall_back_to_remote_session_when_actor_is_not_module_container()
    {
        using var services = BuildServices(new StubActorRuntime(
            new StubActor("agent-1", new PlainAgent("agent-1"))));
        var resolver = CreateResolver(services);

        var session = await resolver.ResolveAsync(new VoicePresenceSessionRequest("agent-1", "voice_presence_openai"));

        session.ShouldNotBeNull();
        session.Module.ShouldBeNull();
        session.IsInitialized.ShouldBeTrue();
        session.PcmSampleRateHz.ShouldBe(16000);
    }

    private static CompositeVoicePresenceSessionResolver CreateResolver(ServiceProvider services) =>
        new(
            new InProcessActorVoicePresenceSessionResolver(services),
            new RemoteActorVoicePresenceSessionResolver(
                services,
            [
                new VoicePresenceModuleRegistration(
                    ["voice_presence_openai"],
                    (_, resolvedName) => CreateModule(resolvedName),
                    pcmSampleRateHz: 16000),
            ]));

    private static ServiceProvider BuildServices(IActorRuntime runtime)
    {
        var services = new ServiceCollection();
        services.AddSingleton(runtime);
        services.AddSingleton<IActorDispatchPort, NoopDispatchPort>();
        services.AddSingleton<IActorEventSubscriptionProvider, NoopSubscriptionProvider>();
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
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => throw new NotSupportedException();

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

    private class PlainAgent(string id) : IAgent
    {
        public string Id => id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(id);

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ModuleAwareAgent(string id, IReadOnlyList<IEventModule<IEventHandlerContext>> modules)
        : PlainAgent(id), IEventModuleContainer<IEventHandlerContext>
    {
        public IReadOnlyList<IEventModule<IEventHandlerContext>> GetModules() => modules;
    }

    private sealed class NoopDispatchPort : IActorDispatchPort
    {
        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopSubscriptionProvider : IActorEventSubscriptionProvider
    {
        public Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new() =>
            Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
