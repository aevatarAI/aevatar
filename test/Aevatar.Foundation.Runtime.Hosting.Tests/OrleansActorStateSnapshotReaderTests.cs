using System.Reflection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansActorStateSnapshotReaderTests
{
    [Fact]
    public async Task GetStateAsync_ShouldAcceptFullNameStateTypeFromRuntimeSnapshot()
    {
        var state = new StringValue { Value = "workflow-catalog" };
        var grain = new SnapshotRuntimeActorGrain(new RuntimeActorStateSnapshot
        {
            AgentTypeName = typeof(object).AssemblyQualifiedName,
            StateTypeName = typeof(StringValue).FullName,
            StateBytes = state.ToByteArray(),
            StateVersion = 3,
        });

        var grainFactory = GrainFactoryProxy.Create(actorId =>
        {
            actorId.Should().Be("actor-1");
            return grain;
        });
        var reader = new OrleansActorStateSnapshotReader(grainFactory);

        var snapshot = await reader.GetStateAsync<StringValue>("actor-1");

        snapshot.Should().NotBeNull();
        snapshot!.Value.Should().Be("workflow-catalog");
    }

    private sealed class SnapshotRuntimeActorGrain : IRuntimeActorGrain
    {
        private readonly RuntimeActorStateSnapshot? _snapshot;

        public SnapshotRuntimeActorGrain(RuntimeActorStateSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<bool> InitializeAgentAsync(string agentTypeName)
        {
            _ = agentTypeName;
            return Task.FromResult(true);
        }

        public Task<bool> IsInitializedAsync() => Task.FromResult(true);

        public Task HandleEnvelopeAsync(byte[] envelopeBytes)
        {
            _ = envelopeBytes;
            return Task.CompletedTask;
        }

        public Task AddChildAsync(string childId)
        {
            _ = childId;
            return Task.CompletedTask;
        }

        public Task RemoveChildAsync(string childId)
        {
            _ = childId;
            return Task.CompletedTask;
        }

        public Task SetParentAsync(string parentId)
        {
            _ = parentId;
            return Task.CompletedTask;
        }

        public Task ClearParentAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetChildrenAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> GetParentAsync() => Task.FromResult<string?>(null);

        public Task<string> GetDescriptionAsync() => Task.FromResult("snapshot");

        public Task<string> GetAgentTypeNameAsync() => Task.FromResult(string.Empty);

        public Task<RuntimeActorStateSnapshot?> GetStateSnapshotAsync() => Task.FromResult(_snapshot);

        public Task DeactivateAsync() => Task.CompletedTask;

        public Task PurgeAsync() => Task.CompletedTask;
    }

    private class GrainFactoryProxy : DispatchProxy
    {
        public Func<string, IRuntimeActorGrain>? ResolveGrain { get; set; }

        public static IGrainFactory Create(Func<string, IRuntimeActorGrain> resolveGrain)
        {
            var proxy = DispatchProxy.Create<IGrainFactory, GrainFactoryProxy>();
            ((GrainFactoryProxy)(object)proxy).ResolveGrain = resolveGrain;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetGrain" &&
                targetMethod.IsGenericMethod &&
                targetMethod.GetGenericArguments().Length == 1 &&
                targetMethod.GetGenericArguments()[0] == typeof(IRuntimeActorGrain) &&
                args is { Length: > 0 } &&
                args[0] is string actorId &&
                ResolveGrain != null)
            {
                return ResolveGrain(actorId);
            }

            throw new NotSupportedException($"Unexpected grain factory call: {targetMethod?.Name}");
        }
    }
}
