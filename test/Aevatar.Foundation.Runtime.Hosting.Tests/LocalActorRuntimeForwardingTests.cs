using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class LocalActorRuntimeForwardingTests
{
    [Fact]
    public async Task DestroyAsync_ShouldCleanupIncomingAndOutgoingForwardingBindings()
    {
        var runtime = CreateRuntime(out var registry, out var scheduler);
        var parent = await runtime.CreateAsync<LocalTestAgent>("parent");
        var middle = await runtime.CreateAsync<LocalTestAgent>("middle");
        var child = await runtime.CreateAsync<LocalTestAgent>("child");

        await runtime.LinkAsync(parent.Id, middle.Id);
        await runtime.LinkAsync(middle.Id, child.Id);

        await runtime.DestroyAsync(middle.Id);

        (await parent.GetChildrenIdsAsync()).Should().NotContain("middle");
        (await child.GetParentIdAsync()).Should().BeNull();
        (await registry.ListBySourceAsync("parent", CancellationToken.None)).Should().BeEmpty();
        (await registry.ListBySourceAsync("middle", CancellationToken.None)).Should().BeEmpty();
        scheduler.PurgedActorIds.Should().ContainSingle("middle");
    }

    private static LocalActorRuntime CreateRuntime(
        out InMemoryStreamForwardingRegistry registry,
        out RecordingCallbackScheduler scheduler)
    {
        registry = new InMemoryStreamForwardingRegistry();
        var streams = new InMemoryStreamProvider(
            new InMemoryStreamOptions(),
            NullLoggerFactory.Instance,
            registry);
        scheduler = new RecordingCallbackScheduler();
        var services = new ServiceCollection()
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .BuildServiceProvider();
        return new LocalActorRuntime(
            streams,
            services,
            streams);
    }

    private sealed class LocalTestAgent : IAgent
    {
        public string Id => "local-test";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("local-test");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<string> PurgedActorIds { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PurgedActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }
}
