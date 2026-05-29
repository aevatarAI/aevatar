using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Maintenance;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Orleans;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class OrleansRetiredActorCleanupCoordinatorSurfaceTests
{
    [Fact]
    public async Task OrleansAgentProxy_ShouldForwardCoordinatorCallsToRuntimeActorGrain()
    {
        var grain = new RecordingRuntimeActorGrain
        {
            AcquireResult = LeaseHandle(),
            CheckResult = true,
        };
        var proxy = new OrleansAgentProxy("cleanup-coordinator", grain, new UnusedStreamProvider());
        var acquire = AcquireCommand("spec-proxy", "owner-proxy");
        var check = CheckCommand("spec-proxy", "owner-proxy");
        var release = ReleaseCommand("spec-proxy", "owner-proxy");
        var failure = FailureCommand("spec-proxy", "owner-proxy");
        using var cancellation = new CancellationTokenSource();

        var acquired = await proxy.TryAcquireLeaseAsync(acquire, cancellation.Token);
        var checkedLease = await proxy.CheckLeaseAsync(check, cancellation.Token);
        await proxy.ReleaseLeaseAsync(release, cancellation.Token);
        await proxy.RecordFailureAsync(failure, cancellation.Token);

        acquired.Should().BeSameAs(grain.AcquireResult);
        checkedLease.Should().BeTrue();
        grain.AcquireCommand.Should().BeSameAs(acquire);
        grain.CheckCommand.Should().BeSameAs(check);
        grain.ReleaseCommand.Should().BeSameAs(release);
        grain.FailureCommand.Should().BeSameAs(failure);
        grain.AcquireToken.Should().Be(cancellation.Token);
        grain.CheckToken.Should().Be(cancellation.Token);
        grain.ReleaseToken.Should().Be(cancellation.Token);
        grain.FailureToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task RuntimeActorGrain_ShouldDelegateCoordinatorCallsToBoundCoordinatorActor()
    {
        var grain = CreateRuntimeActorGrain("cleanup-coordinator");
        var coordinator = new RecordingCoordinatorAgent
        {
            AcquireResult = LeaseHandle(),
            CheckResult = true,
        };
        BindAgent(grain, coordinator);
        var acquire = AcquireCommand("spec-grain", "owner-grain");
        var check = CheckCommand("spec-grain", "owner-grain");
        var release = ReleaseCommand("spec-grain", "owner-grain");
        var failure = FailureCommand("spec-grain", "owner-grain");
        using var cancellation = new CancellationTokenSource();

        var acquired = await grain.TryAcquireRetiredActorCleanupLeaseAsync(acquire, cancellation.Token);
        var checkedLease = await grain.CheckRetiredActorCleanupLeaseAsync(check, cancellation.Token);
        await grain.ReleaseRetiredActorCleanupLeaseAsync(release, cancellation.Token);
        await grain.RecordRetiredActorCleanupFailureAsync(failure, cancellation.Token);

        acquired.Should().BeSameAs(coordinator.AcquireResult);
        checkedLease.Should().BeTrue();
        coordinator.AcquireCommand.Should().BeSameAs(acquire);
        coordinator.CheckCommand.Should().BeSameAs(check);
        coordinator.ReleaseCommand.Should().BeSameAs(release);
        coordinator.FailureCommand.Should().BeSameAs(failure);
        coordinator.AcquireToken.Should().Be(cancellation.Token);
        coordinator.CheckToken.Should().Be(cancellation.Token);
        coordinator.ReleaseToken.Should().Be(cancellation.Token);
        coordinator.FailureToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task RuntimeActorGrain_WhenBoundAgentIsNotCoordinator_ShouldThrowUnsupportedContractError()
    {
        var grain = CreateRuntimeActorGrain("non-coordinator");
        BindAgent(grain, new NonCoordinatorAgent("non-coordinator"));
        var command = AcquireCommand("spec-failure", "owner-failure");

        Func<Task> act = async () =>
            await grain.TryAcquireRetiredActorCleanupLeaseAsync(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Actor 'non-coordinator' does not expose the retired actor cleanup coordinator contract.");
    }

    private static RuntimeActorGrain CreateRuntimeActorGrain(string actorId)
    {
        var state = DispatchProxy.Create<IPersistentState<RuntimeActorGrainState>, RuntimeActorPersistentStateProxy>();
        var grain = new RuntimeActorGrain(state);
        var context = DispatchProxy.Create<IGrainContext, GrainContextProxy>();
        ((GrainContextProxy)(object)context).GrainId = GrainId.Create("runtimeactorgrain", actorId);

        typeof(Grain).GetField("<GrainContext>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(grain, context);

        return grain;
    }

    private static void BindAgent(RuntimeActorGrain grain, IAgent agent) =>
        typeof(RuntimeActorGrain).GetField("_agent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(grain, agent);

    private static RetiredActorCleanupAcquireCommand AcquireCommand(string specId, string ownerId) =>
        new()
        {
            SpecId = specId,
            OwnerId = ownerId,
            RequestedToken = "requested-token",
            RequestedAt = Timestamp.FromDateTimeOffset(Now()),
            ExpiresAt = Timestamp.FromDateTimeOffset(Now().AddMinutes(5)),
        };

    private static RetiredActorCleanupCheckCommand CheckCommand(string specId, string ownerId) =>
        new()
        {
            SpecId = specId,
            Epoch = 7,
            Token = "lease-token",
            OwnerId = ownerId,
            CheckedAt = Timestamp.FromDateTimeOffset(Now()),
        };

    private static RetiredActorCleanupReleaseCommand ReleaseCommand(string specId, string ownerId) =>
        new()
        {
            SpecId = specId,
            Epoch = 7,
            Token = "lease-token",
            OwnerId = ownerId,
            ReleasedAt = Timestamp.FromDateTimeOffset(Now()),
        };

    private static RetiredActorCleanupFailureCommand FailureCommand(string specId, string ownerId) =>
        new()
        {
            SpecId = specId,
            Epoch = 7,
            Token = "lease-token",
            OwnerId = ownerId,
            Error = "failure",
            FailedAt = Timestamp.FromDateTimeOffset(Now()),
        };

    private static RetiredActorCleanupLeaseHandle LeaseHandle() =>
        new("spec", 7, "lease-token", "owner", Now(), Now().AddMinutes(5));

    private static DateTimeOffset Now() => new(2026, 05, 29, 12, 00, 00, TimeSpan.Zero);

    private sealed class RecordingRuntimeActorGrain : IRuntimeActorGrain
    {
        public RetiredActorCleanupLeaseHandle? AcquireResult { get; init; }
        public bool CheckResult { get; init; }
        public RetiredActorCleanupAcquireCommand? AcquireCommand { get; private set; }
        public RetiredActorCleanupCheckCommand? CheckCommand { get; private set; }
        public RetiredActorCleanupReleaseCommand? ReleaseCommand { get; private set; }
        public RetiredActorCleanupFailureCommand? FailureCommand { get; private set; }
        public CancellationToken AcquireToken { get; private set; }
        public CancellationToken CheckToken { get; private set; }
        public CancellationToken ReleaseToken { get; private set; }
        public CancellationToken FailureToken { get; private set; }

        public Task<RetiredActorCleanupLeaseHandle?> TryAcquireRetiredActorCleanupLeaseAsync(
            RetiredActorCleanupAcquireCommand command,
            CancellationToken ct = default)
        {
            AcquireCommand = command;
            AcquireToken = ct;
            return Task.FromResult(AcquireResult);
        }

        public Task<bool> CheckRetiredActorCleanupLeaseAsync(
            RetiredActorCleanupCheckCommand command,
            CancellationToken ct = default)
        {
            CheckCommand = command;
            CheckToken = ct;
            return Task.FromResult(CheckResult);
        }

        public Task ReleaseRetiredActorCleanupLeaseAsync(
            RetiredActorCleanupReleaseCommand command,
            CancellationToken ct = default)
        {
            ReleaseCommand = command;
            ReleaseToken = ct;
            return Task.CompletedTask;
        }

        public Task RecordRetiredActorCleanupFailureAsync(
            RetiredActorCleanupFailureCommand command,
            CancellationToken ct = default)
        {
            FailureCommand = command;
            FailureToken = ct;
            return Task.CompletedTask;
        }

        public Task<bool> InitializeAgentAsync(string agentTypeName) => Task.FromResult(true);
        public Task<bool> InitializeAgentByKindAsync(string kind) => Task.FromResult(true);
        public Task<bool> IsInitializedAsync() => Task.FromResult(true);
        public Task HandleEnvelopeAsync(byte[] envelopeBytes) => Task.CompletedTask;
        public Task AddChildAsync(string childId) => Task.CompletedTask;
        public Task RemoveChildAsync(string childId) => Task.CompletedTask;
        public Task SetParentAsync(string parentId) => Task.CompletedTask;
        public Task ClearParentAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetChildrenAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> GetParentAsync() => Task.FromResult<string?>(null);
        public Task<string> GetDescriptionAsync() => Task.FromResult("recording");
        public Task<string> GetAgentTypeNameAsync() => Task.FromResult(string.Empty);
        public Task<string> GetAgentKindAsync() => Task.FromResult(string.Empty);
        public Task DeactivateAsync() => Task.CompletedTask;
        public Task PurgeAsync() => Task.CompletedTask;
    }

    private sealed class RecordingCoordinatorAgent : NonCoordinatorAgent, IRetiredActorCleanupCoordinatorActor
    {
        public RecordingCoordinatorAgent()
            : base("cleanup-coordinator")
        {
        }

        public RetiredActorCleanupLeaseHandle? AcquireResult { get; init; }
        public bool CheckResult { get; init; }
        public RetiredActorCleanupAcquireCommand? AcquireCommand { get; private set; }
        public RetiredActorCleanupCheckCommand? CheckCommand { get; private set; }
        public RetiredActorCleanupReleaseCommand? ReleaseCommand { get; private set; }
        public RetiredActorCleanupFailureCommand? FailureCommand { get; private set; }
        public CancellationToken AcquireToken { get; private set; }
        public CancellationToken CheckToken { get; private set; }
        public CancellationToken ReleaseToken { get; private set; }
        public CancellationToken FailureToken { get; private set; }

        public Task<RetiredActorCleanupLeaseHandle?> TryAcquireLeaseAsync(
            RetiredActorCleanupAcquireCommand command,
            CancellationToken ct = default)
        {
            AcquireCommand = command;
            AcquireToken = ct;
            return Task.FromResult(AcquireResult);
        }

        public Task<bool> CheckLeaseAsync(
            RetiredActorCleanupCheckCommand command,
            CancellationToken ct = default)
        {
            CheckCommand = command;
            CheckToken = ct;
            return Task.FromResult(CheckResult);
        }

        public Task ReleaseLeaseAsync(
            RetiredActorCleanupReleaseCommand command,
            CancellationToken ct = default)
        {
            ReleaseCommand = command;
            ReleaseToken = ct;
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(
            RetiredActorCleanupFailureCommand command,
            CancellationToken ct = default)
        {
            FailureCommand = command;
            FailureToken = ct;
            return Task.CompletedTask;
        }
    }

    private class NonCoordinatorAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("non-coordinator");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class UnusedStreamProvider : Aevatar.Foundation.Abstractions.IStreamProvider
    {
        public IStream GetStream(string actorId) =>
            throw new NotSupportedException("The coordinator surface tests do not publish stream events.");
    }

    private class GrainContextProxy : DispatchProxy
    {
        public GrainId GrainId { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            if (targetMethod?.Name == "get_GrainId")
                return GrainId;

            return GetDefault(targetMethod?.ReturnType);
        }
    }

    private class RuntimeActorPersistentStateProxy : DispatchProxy
    {
        public RuntimeActorGrainState State { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name;
            if (name == "get_State")
                return State;
            if (name == "set_State")
            {
                State = args?[0] as RuntimeActorGrainState ?? new RuntimeActorGrainState();
                return null;
            }

            if (name is "ReadStateAsync" or "WriteStateAsync" or "ClearStateAsync")
                return Task.CompletedTask;
            if (name == "get_RecordExists")
                return true;
            if (name == "get_Etag")
                return string.Empty;
            if (name == "set_Etag")
                return null;

            return GetDefault(targetMethod?.ReturnType);
        }
    }

    private static object? GetDefault(System.Type? type)
    {
        if (type == null || type == typeof(void))
            return null;

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
