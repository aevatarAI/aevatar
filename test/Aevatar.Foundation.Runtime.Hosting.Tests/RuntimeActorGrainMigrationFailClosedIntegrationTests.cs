using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

/// <summary>
/// #3482 hard gate at grain level: a state-schema migration write that fails, or whose durable
/// outcome is unknown, must leave the actor unavailable — never activated at the old schema,
/// never partially migrated, inbox untouched — until a later activation re-reads durable state.
/// Runs a real silo with a fault-injecting grain storage for the actor state slot.
/// </summary>
public sealed class RuntimeActorGrainMigrationFailClosedIntegrationTests
{
    private const string MigratedKind = "integrationtests.migrated-fail-closed";
    private const string ContractId = "test.contract.v1";

    [Fact]
    public async Task Activation_WhenMigrationWriteFailsBeforeCommit_ShouldStayUnavailableAndNotBind()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var storage = new FaultInjectingAgentStateStorage();
        var recorder = new MigrationFixtureRecorder();
        storage.Seed(actorId, LegacyRow(actorId));
        storage.Mode = StorageFaultMode.FailBeforeCommit;
        var host = await StartSiloHostAsync(storage, recorder);

        try
        {
            var grain = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IRuntimeActorGrain>(actorId);

            var activation = () => grain.GetAgentKindAsync();
            await activation.Should().ThrowAsync<Exception>("the activation must fail closed");

            // Every activation attempt (Orleans may retry a failed activation) re-read the durable
            // v0 row and tried the write once; none of them constructed or bound the agent.
            recorder.Activations.Should().Be(0, "the agent must not be constructed or bound");
            recorder.ConstructedSchemaVersions.Should().BeEmpty();
            var failedAttempts = storage.WriteCount(actorId);
            failedAttempts.Should().BeGreaterThanOrEqualTo(1);
            storage.Read(actorId).Identity!.StateSchemaVersion.Should().Be(0, "nothing was committed");

            // Storage recovers: the next activation re-reads the durable v0 row and migrates it.
            storage.Mode = StorageFaultMode.None;
            (await grain.GetAgentKindAsync()).Should().Be(MigratedKind);
            recorder.ConstructedSchemaVersions.Should().Equal(1);
            recorder.ActivatedSchemaVersions.Should().Equal(1);
            storage.WriteCount(actorId).Should().Be(failedAttempts + 1);
            storage.Read(actorId).Identity!.StateSchemaVersion.Should().Be(1);
            storage.Read(actorId).Identity!.StateSchemaAdoptions.Should().ContainSingle()
                .Which.StateSchemaVersion.Should().Be(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Activation_WhenMigrationCommitsButAcknowledgementFails_ShouldNotServeOldSchema_AndReactivateAsDurableV1()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var storage = new FaultInjectingAgentStateStorage();
        var recorder = new MigrationFixtureRecorder();
        storage.Seed(actorId, LegacyRow(actorId));
        storage.Mode = StorageFaultMode.CommitThenThrow;
        var host = await StartSiloHostAsync(storage, recorder);

        try
        {
            var grain = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IRuntimeActorGrain>(actorId);

            // The activation that observed the lost acknowledgement is discarded (Orleans may
            // retry the activation right away, so the call itself may or may not fault). What must
            // hold: the actor never ran at the old schema, the committed v1 row is what any later
            // activation reads, and nothing is rewritten.
            await TryCallAsync(() => grain.GetAgentKindAsync());
            storage.WriteCount(actorId).Should().Be(1, "the only write is the one that committed v1");
            storage.Read(actorId).Identity!.StateSchemaVersion.Should().Be(1, "the store did commit v1");
            recorder.ConstructedSchemaVersions.Should().NotContain(0,
                "construction must happen only after the durable schema is current");
            recorder.ActivatedSchemaVersions.Should().NotContain(0, "no activation may serve the pre-migration schema");

            storage.Mode = StorageFaultMode.None;
            (await grain.GetAgentKindAsync()).Should().Be(MigratedKind);
            recorder.ActivatedSchemaVersions.Should().Equal(1);
            storage.WriteCount(actorId).Should().Be(1, "v1 was already durable; nothing is rewritten");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task Inbox_WhenMigrationPersistenceFails_ShouldNotBeConsumedUntilMigrationSucceeds()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var storage = new FaultInjectingAgentStateStorage();
        var recorder = new MigrationFixtureRecorder();
        storage.Seed(actorId, LegacyRow(actorId));
        storage.Mode = StorageFaultMode.FailBeforeCommit;
        var host = await StartSiloHostAsync(storage, recorder);

        try
        {
            var grain = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IRuntimeActorGrain>(actorId);
            var envelope = new EventEnvelope
            {
                Id = "inbox-1",
                Route = EnvelopeRouteSemantics.CreateDirect("test-publisher", actorId),
            };

            var delivery = () => grain.HandleEnvelopeAsync(envelope.ToByteArray());
            await delivery.Should().ThrowAsync<Exception>();
            recorder.HandledEnvelopes.Should().BeEmpty("no envelope may reach an unmigrated actor");
            recorder.Activations.Should().Be(0);
            recorder.ConstructedSchemaVersions.Should().BeEmpty();
            storage.Read(actorId).Identity!.StateSchemaVersion.Should().Be(0);

            storage.Mode = StorageFaultMode.None;
            await grain.HandleEnvelopeAsync(envelope.ToByteArray());
            recorder.HandledEnvelopes.Should().Equal("inbox-1");
            recorder.ConstructedSchemaVersions.Should().Equal(1);
            recorder.ActivatedSchemaVersions.Should().Equal(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ActiveLegacySchema_WhenAdmissionAppears_ShouldTurnOverBeforeHandlingAndAcceptRedeliveryOnce()
    {
        var actorId = $"actor-{Guid.NewGuid():N}";
        var storage = new FaultInjectingAgentStateStorage();
        var recorder = new MigrationFixtureRecorder();
        var admission = new MutableAdmissionReader();
        storage.Seed(actorId, LegacyRow(actorId));
        var host = await StartSiloHostAsync(storage, recorder, admission);

        try
        {
            var grain = host.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(actorId);
            (await grain.GetAgentKindAsync()).Should().Be(MigratedKind);
            recorder.ConstructedSchemaVersions.Should().Equal(0);
            recorder.ActivatedSchemaVersions.Should().Equal(0);
            storage.WriteCount(actorId).Should().Be(0);

            admission.Current = OpenAdmissionReader.CreateAdmission();
            var envelope = new EventEnvelope
            {
                Id = "turnover-envelope",
                Route = EnvelopeRouteSemantics.CreateDirect("test-publisher", actorId),
            };

            var firstDelivery = () => grain.HandleEnvelopeAsync(envelope.ToByteArray());
            var turnover = await firstDelivery.Should().ThrowAsync<Exception>();
            ContainsTurnoverRequired(turnover.Which).Should().BeTrue(
                "the transport must observe a retryable turnover failure");
            recorder.HandledEnvelopes.Should().BeEmpty(
                "the schema-zero activation must not consume the envelope");

            await recorder.FirstDeactivation.WaitAsync(TimeSpan.FromSeconds(10));
            await grain.HandleEnvelopeAsync(envelope.ToByteArray());

            recorder.ConstructedSchemaVersions.Should().Equal(0, 1);
            recorder.ActivatedSchemaVersions.Should().Equal(0, 1);
            recorder.HandledEnvelopes.Should().Equal("turnover-envelope");
            storage.WriteCount(actorId).Should().Be(1);
            var durable = storage.Read(actorId);
            durable.Identity!.StateSchemaVersion.Should().Be(1);
            durable.Identity.StateSchemaAdoptions.Should().ContainSingle();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task TryCallAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception)
        {
            // The activation faulted and Orleans surfaced it; the assertions below cover
            // the durable/observable semantics either way.
        }
    }

    private static bool ContainsTurnoverRequired(Exception exception) =>
        exception is RuntimeActorStateSchemaTurnoverRequiredException ||
        (exception is AggregateException aggregate &&
         aggregate.InnerExceptions.Any(ContainsTurnoverRequired)) ||
        (exception.InnerException != null &&
         ContainsTurnoverRequired(exception.InnerException));

    private static RuntimeActorGrainState LegacyRow(string actorId) =>
        new()
        {
            AgentId = actorId,
            Identity = new RuntimeActorIdentity { Kind = MigratedKind, StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = new EventEnvelope { Id = "v0" }.ToByteArray(),
        };

    private static async Task<IHost> StartSiloHostAsync(
        FaultInjectingAgentStateStorage storage,
        MigrationFixtureRecorder recorder,
        IRuntimeFleetCapabilityAdmissionReader? admissionReader = null)
    {
        var serviceId = $"aevatar-migration-fail-closed-service-{Guid.NewGuid():N}";
        var clusterId = $"aevatar-migration-fail-closed-cluster-{Guid.NewGuid():N}";

        return await SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(ports.SiloPort, ports.GatewayPort, null, serviceId, clusterId);
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(recorder);
                    services.AddAevatarAgentKindRegistry(builder =>
                        builder.Register(new AgentRegistration(
                            MigratedKind,
                            typeof(MigrationFixtureAgent),
                            typeof(EventEnvelope),
                            StateSchemaVersion: 1,
                            StateMigrationTypes: [typeof(MigrationFixtureV0ToV1Migration)])));
                    services.Replace(ServiceDescriptor.Singleton<IRuntimeFleetCapabilityAdmissionReader>(
                        admissionReader ?? new OpenAdmissionReader()));
                    services.Replace(ServiceDescriptor.Singleton<IRuntimeLocalMembershipIdentityReader>(
                        new FixedMembershipReader()));
                    services.RemoveAllKeyed<IGrainStorage>(OrleansRuntimeConstants.GrainStateStorageName);
                    services.AddSingleton(storage);
                    services.AddGrainStorage<FaultInjectingAgentStateStorage>(
                        OrleansRuntimeConstants.GrainStateStorageName,
                        (sp, _) => sp.GetRequiredService<FaultInjectingAgentStateStorage>());
                });
            })
            .Build());
    }

    private enum StorageFaultMode
    {
        None,
        FailBeforeCommit,
        CommitThenThrow,
    }

    /// <summary>
    /// The actor state slot ("agent" storage) with write faults: either the write never reaches
    /// storage, or it commits and the acknowledgement is lost.
    /// </summary>
    private sealed class FaultInjectingAgentStateStorage : IGrainStorage
    {
        private readonly ConcurrentDictionary<string, RuntimeActorGrainState> _rows = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RuntimeActorCommittedStatePublicationGrainState>
            _publicationRows = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _writes = new(StringComparer.Ordinal);

        public StorageFaultMode Mode { get; set; }

        public void Seed(string actorId, RuntimeActorGrainState state) => _rows[actorId] = Clone(state);

        public RuntimeActorGrainState Read(string actorId) => Clone(_rows[actorId]);

        public int WriteCount(string actorId) => _writes.GetValueOrDefault(actorId);

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var actorId = grainId.Key.ToString()!;
            if (typeof(T) == typeof(RuntimeActorGrainState) &&
                _rows.TryGetValue(actorId, out var row))
            {
                grainState.State = (T)(object)Clone(row);
                grainState.RecordExists = true;
                grainState.ETag = string.Empty;
            }
            else if (typeof(T) == typeof(RuntimeActorCommittedStatePublicationGrainState) &&
                     _publicationRows.TryGetValue(actorId, out var publicationRow))
            {
                grainState.State = (T)(object)Clone(publicationRow);
                grainState.RecordExists = true;
                grainState.ETag = string.Empty;
            }

            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var actorId = grainId.Key.ToString()!;
            if (grainState.State is RuntimeActorCommittedStatePublicationGrainState publicationState)
            {
                _publicationRows[actorId] = Clone(publicationState);
                grainState.RecordExists = true;
                grainState.ETag = string.Empty;
                return Task.CompletedTask;
            }

            if (grainState.State is not RuntimeActorGrainState runtimeState)
                throw new InvalidOperationException($"Unsupported test grain state '{typeof(T).FullName}'.");

            _writes.AddOrUpdate(actorId, 1, static (_, count) => count + 1);
            switch (Mode)
            {
                case StorageFaultMode.FailBeforeCommit:
                    throw new IOException("actor state store unavailable before commit");
                case StorageFaultMode.CommitThenThrow:
                    _rows[actorId] = Clone(runtimeState);
                    throw new IOException("actor state store committed but the acknowledgement was lost");
                default:
                    _rows[actorId] = Clone(runtimeState);
                    grainState.RecordExists = true;
                    grainState.ETag = string.Empty;
                    return Task.CompletedTask;
            }
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var actorId = grainId.Key.ToString()!;
            if (typeof(T) == typeof(RuntimeActorCommittedStatePublicationGrainState))
                _publicationRows.TryRemove(actorId, out _);
            else
                _rows.TryRemove(actorId, out _);
            grainState.RecordExists = false;
            grainState.ETag = string.Empty;
            return Task.CompletedTask;
        }

        private static RuntimeActorGrainState Clone(RuntimeActorGrainState state) =>
            new()
            {
                AgentId = state.AgentId,
                ParentId = state.ParentId,
                Children = [.. state.Children],
                AgentStateTypeName = state.AgentStateTypeName,
                AgentStateSnapshot = state.AgentStateSnapshot?.ToArray(),
                AgentStateSnapshotVersion = state.AgentStateSnapshotVersion,
                Identity = state.Identity?.Clone(),
                CommittedStatePublicationState = state.CommittedStatePublicationState?.ToArray(),
            };

        private static RuntimeActorCommittedStatePublicationGrainState Clone(
            RuntimeActorCommittedStatePublicationGrainState state) =>
            new()
            {
                Checkpoint = state.Checkpoint?.ToArray(),
            };
    }

    /// <summary>
    /// One silo hosts one fixture actor per test, so the recorder is host-scoped: it records
    /// every agent activation (with the schema version bound by the runtime) and every envelope
    /// the agent actually handled.
    /// </summary>
    public sealed class MigrationFixtureRecorder
    {
        private readonly ConcurrentQueue<int> _activations = new();
        private readonly ConcurrentQueue<int> _constructions = new();
        private readonly ConcurrentQueue<string> _envelopes = new();
        private readonly TaskCompletionSource<bool> _firstDeactivation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RecordConstruction(int schemaVersion) =>
            _constructions.Enqueue(schemaVersion);

        public void RecordActivation(int schemaVersion) => _activations.Enqueue(schemaVersion);

        public void RecordDeactivation() => _firstDeactivation.TrySetResult(true);

        public void RecordEnvelope(string envelopeId) => _envelopes.Enqueue(envelopeId);

        public int Activations => _activations.Count;

        public IReadOnlyList<int> ConstructedSchemaVersions => [.. _constructions];

        public IReadOnlyList<int> ActivatedSchemaVersions => [.. _activations];

        public IReadOnlyList<string> HandledEnvelopes => [.. _envelopes];

        public Task FirstDeactivation => _firstDeactivation.Task;
    }

    public sealed class MigrationFixtureAgent : IAgent
    {
        private readonly MigrationFixtureRecorder _recorder;
        private readonly IRuntimeActorStateSchemaContextReader _schemaContext;

        public MigrationFixtureAgent(
            MigrationFixtureRecorder recorder,
            IRuntimeActorStateSchemaContextReader schemaContext)
        {
            _recorder = recorder;
            _schemaContext = schemaContext;
            _recorder.RecordConstruction(
                _schemaContext.Current?.StateSchemaVersion ?? -1);
        }

        public string Id => "migration-fixture";

        public Task ActivateAsync(CancellationToken ct = default)
        {
            _recorder.RecordActivation(
                _schemaContext.Current?.StateSchemaVersion ?? -1);
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            _recorder.RecordDeactivation();
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult(nameof(MigrationFixtureAgent));

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>(Array.Empty<Type>());

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _recorder.RecordEnvelope(envelope.Id);
            return Task.CompletedTask;
        }
    }

    [ActorStateMigration(
        MigratedKind,
        RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
        RequiredContractId = ContractId,
        RequiredContractVersion = 1)]
    public sealed class MigrationFixtureV0ToV1Migration : IActorStateMigration<EventEnvelope>
    {
        public int FromStateVersion => 0;

        public int ToStateVersion => 1;

        public EventEnvelope Apply(EventEnvelope state)
        {
            var next = state.Clone();
            next.Id = "v1";
            return next;
        }
    }

    private sealed class OpenAdmissionReader : IRuntimeFleetCapabilityAdmissionReader
    {
        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (capability != RuntimeFleetCapability.WorkflowNormalizedStateWritesV1)
                return Task.FromResult<RuntimeFleetCapabilityAdmission?>(null);

            return Task.FromResult<RuntimeFleetCapabilityAdmission?>(CreateAdmission());
        }

        internal static RuntimeFleetCapabilityAdmission CreateAdmission()
        {
            var now = DateTimeOffset.UtcNow;
            var admission = new RuntimeFleetCapabilityAdmission
            {
                Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                Status = RuntimeFleetCapabilityGateStatus.Open,
                AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                AuthorityStateVersion = 9,
                CapabilityEpoch = 3,
                MembershipEpoch = 7,
                DeploymentRevision = "revision-a",
                MinimumReaderContractVersion = 1,
                MembershipObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddSeconds(-5)),
                MembershipValidUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddMinutes(1)),
                ActiveMemberCount = 1,
                ConfirmedMemberCount = 1,
                MembershipDigest = "digest-a",
                ContractId = ContractId,
            };
            admission.AdmittedMembers.Add(new RuntimeFleetAdmittedMember { MemberId = "member-a", Incarnation = "inc-a" });
            return admission;
        }
    }

    private sealed class MutableAdmissionReader : IRuntimeFleetCapabilityAdmissionReader
    {
        public RuntimeFleetCapabilityAdmission? Current { get; set; }

        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                Current?.Capability == capability ? Current.Clone() : null);
        }
    }

    private sealed class FixedMembershipReader : IRuntimeLocalMembershipIdentityReader
    {
        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RuntimeLocalMembershipIdentity?>(
                new RuntimeLocalMembershipIdentity(7, "digest-a", "revision-a", "member-a", "inc-a"));
        }
    }
}
