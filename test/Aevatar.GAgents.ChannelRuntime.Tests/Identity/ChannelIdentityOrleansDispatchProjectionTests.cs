using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class ChannelIdentityOrleansDispatchProjectionTests
{
    // Refactor (iter290/cluster517-first):
    //   Old pattern: channel identity tests covered handler bodies and a commit-only Orleans projection path.
    //   New principle: Orleans dispatch/projection tests cover commit, revoke delete, and duplicate commit without projection-only no-op events.
    [Fact]
    public async Task CommitBindingDispatch_ShouldReplayCommittedEventToChannelIdentityReadModel()
    {
        var observer = new ObservingExternalIdentityBindingWriter();
        using var host = await StartSiloHostAsync(observer);
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "tenant-issue1313",
            ExternalUserId = $"user-{Guid.NewGuid():N}",
        };
        var actorId = subject.ToActorId();
        await EnsureExternalIdentityBindingProjectionReadyAsync(host, actorId);

        var dispatch = host.Services.GetRequiredService<
            ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>();

        var result = await dispatch.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd-issue1313-first",
            OwnerScopeId = "owner-user-1",
        });

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().NotBeNull();
        result.Receipt!.ActorId.Should().Be(actorId);

        var projected = await observer.WaitForUpsertAsync(actorId, TestTimeout);

        projected.Id.Should().Be(actorId);
        projected.ActorId.Should().Be(actorId);
        projected.BindingId.Should().Be("bnd-issue1313-first");
        projected.IsActive.Should().BeTrue();
        projected.ExternalSubject.Should().BeEquivalentTo(subject);
        projected.StateVersion.Should().Be(1);
        projected.LastEventId.Should().NotBeNullOrWhiteSpace();

        var reader = host.Services.GetRequiredService<IProjectionDocumentReader<ExternalIdentityBindingDocument, string>>();
        var stored = await reader.GetAsync(actorId);
        stored.Should().BeEquivalentTo(projected);
    }

    [Fact]
    public async Task RevokeBindingDispatch_ShouldDeleteProjectionAndResolveNull()
    {
        var observer = new ObservingExternalIdentityBindingWriter();
        using var host = await StartSiloHostAsync(observer);
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "tenant-issue1355-revoke",
            ExternalUserId = $"user-{Guid.NewGuid():N}",
        };
        var actorId = subject.ToActorId();
        await EnsureExternalIdentityBindingProjectionReadyAsync(host, actorId);

        var commitDispatch = host.Services.GetRequiredService<
            ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>();
        var revokeDispatch = host.Services.GetRequiredService<
            ICommandDispatchService<RevokeBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>();

        var commit = await commitDispatch.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd-issue1355-revoke",
            OwnerScopeId = "owner-user-1",
        });
        commit.Succeeded.Should().BeTrue();

        var upserted = await observer.WaitForUpsertAsync(actorId, TestTimeout);
        upserted.BindingId.Should().Be("bnd-issue1355-revoke");

        var revoke = await revokeDispatch.DispatchAsync(new RevokeBindingCommand
        {
            ExternalSubject = subject,
            Reason = "issue1355-test",
        });
        revoke.Succeeded.Should().BeTrue();

        await observer.WaitForDeleteAsync(actorId, TestTimeout);

        var queryPort = host.Services.GetRequiredService<IExternalIdentityBindingQueryPort>();
        var resolved = await queryPort.ResolveAsync(subject);
        resolved.Should().BeNull();

        var reader = host.Services.GetRequiredService<IProjectionDocumentReader<ExternalIdentityBindingDocument, string>>();
        var stored = await reader.GetAsync(actorId);
        stored.Should().BeNull();

        observer.Snapshot(actorId)
            .Should()
            .Equal(
                BindingWriteObservation.Upsert(actorId, "bnd-issue1355-revoke", 1),
                BindingWriteObservation.Delete(actorId));
    }

    [Fact]
    public async Task DuplicateCommitDispatch_ShouldNotOverwriteFirstBinding()
    {
        var observer = new ObservingExternalIdentityBindingWriter();
        using var host = await StartSiloHostAsync(observer);
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "tenant-issue1355-duplicate",
            ExternalUserId = $"user-{Guid.NewGuid():N}",
        };
        var actorId = subject.ToActorId();
        await EnsureExternalIdentityBindingProjectionReadyAsync(host, actorId);

        var commitDispatch = host.Services.GetRequiredService<
            ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>();
        var revokeDispatch = host.Services.GetRequiredService<
            ICommandDispatchService<RevokeBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>();

        var firstCommit = await commitDispatch.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd-issue1355-first",
            OwnerScopeId = "owner-user-1",
        });
        firstCommit.Succeeded.Should().BeTrue();

        var firstProjected = await observer.WaitForUpsertAsync(actorId, TestTimeout);
        firstProjected.BindingId.Should().Be("bnd-issue1355-first");
        firstProjected.StateVersion.Should().Be(1);

        var duplicateCommit = await commitDispatch.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd-issue1355-second",
            OwnerScopeId = "owner-user-1",
        });
        duplicateCommit.Succeeded.Should().BeTrue();

        var revoke = await revokeDispatch.DispatchAsync(new RevokeBindingCommand
        {
            ExternalSubject = subject,
            Reason = "issue1355-duplicate-barrier",
        });
        revoke.Succeeded.Should().BeTrue();

        await observer.WaitForDeleteAsync(actorId, TestTimeout);

        var observations = observer.Snapshot(actorId);
        observations
            .Where(static observation => observation.Operation == nameof(BindingWriteObservation.Upsert))
            .Should()
            .OnlyContain(
                observation => observation == BindingWriteObservation.Upsert(actorId, "bnd-issue1355-first", 1),
                "at-least-once projection replay may repeat the first write but must never project the duplicate binding");
        observations.Should().Contain(BindingWriteObservation.Upsert(actorId, "bnd-issue1355-first", 1));
        observations.Should().Contain(BindingWriteObservation.Delete(actorId));

        var queryPort = host.Services.GetRequiredService<IExternalIdentityBindingQueryPort>();
        var resolved = await queryPort.ResolveAsync(subject);
        resolved.Should().BeNull();
    }

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(20);

    private static async Task EnsureExternalIdentityBindingProjectionReadyAsync(IHost host, string actorId)
    {
        var targetActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            actorId,
            ChannelIdentityCommittedStateProjectionActivationPlanProvider.ExternalIdentityBindingProjectionKind,
            ProjectionRuntimeMode.DurableMaterialization));
        var forwardingObserver = host.Services.GetRequiredService<ObservingStreamForwardingRegistry>();
        var relayReady = forwardingObserver.WaitForUpsertAsync(actorId, targetActorId, TestTimeout);
        var activation = host.Services.GetRequiredService<
            IProjectionScopeActivationService<ExternalIdentityBindingMaterializationRuntimeLease>>();
        await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = actorId,
            ProjectionKind = ChannelIdentityCommittedStateProjectionActivationPlanProvider.ExternalIdentityBindingProjectionKind,
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        });

        await relayReady;
    }

    private static Task<IHost> StartSiloHostAsync(ObservingExternalIdentityBindingWriter observer) =>
        SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: ports.SiloPort,
                    gatewayPort: ports.GatewayPort,
                    serviceId: $"aevatar-channel-identity-issue1313-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-channel-identity-issue1313-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                {
                    var forwardingObserver = new ObservingStreamForwardingRegistry();
                    var configuration = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            [$"{AevatarOAuthClientBootstrapOptions.SectionName}:Enabled"] = "false",
                            [AevatarOAuthClientOptions.ClientIdConfigurationKey] = "channel-identity-projection-test-client",
                            ["Aevatar:NyxId:InternalApiBaseUrl"] = "http://nyxid.internal.test",
                            [NyxIdBrokerOptions.ResourceServerBaseUrlConfigurationKey] = "https://nyxid.test",
                        })
                        .Build();
                    services.AddSingleton(forwardingObserver);
                    services.AddChannelIdentity(configuration);
                    services.AddInMemoryDocumentProjectionStore<ExternalIdentityBindingDocument, string>(
                        static document => document.Id,
                        static key => key);
                    services.AddInMemoryDocumentProjectionStore<AevatarOAuthClientDocument, string>(
                        static document => document.Id,
                        static key => key);
                    services.DecorateStreamForwardingRegistry(forwardingObserver);
                    services.DecorateExternalIdentityBindingWriter(observer);
                });
            })
            .Build());

    internal sealed class ObservingStreamForwardingRegistry :
        IStreamForwardingRegistry,
        IStreamForwardingBindingAuthority
    {
        private readonly object _gate = new();
        private readonly HashSet<(string SourceStreamId, string TargetStreamId)> _observedUpserts = [];
        private readonly Dictionary<(string SourceStreamId, string TargetStreamId), TaskCompletionSource<StreamForwardingBinding>>
            _nextUpsertsByKey = new();

        private IStreamForwardingRegistry? _registryInner;
        private IStreamForwardingBindingAuthority? _authorityInner;

        public void SetInner(IStreamForwardingRegistry registryInner, IStreamForwardingBindingAuthority authorityInner)
        {
            _registryInner = registryInner ?? throw new ArgumentNullException(nameof(registryInner));
            _authorityInner = authorityInner ?? throw new ArgumentNullException(nameof(authorityInner));
        }

        public async Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            EnsureInner();
            await _registryInner!.UpsertAsync(binding, ct);

            TaskCompletionSource<StreamForwardingBinding> completion;
            var key = (binding.SourceStreamId, binding.TargetStreamId);
            lock (_gate)
            {
                _observedUpserts.Add(key);
                completion = ConsumeNextUpsertCompletion(key);
            }

            completion.TrySetResult(CloneBinding(binding));
        }

        public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
        {
            EnsureInner();
            return _registryInner!.RemoveAsync(sourceStreamId, targetStreamId, ct);
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(
            string sourceStreamId,
            CancellationToken ct = default)
        {
            EnsureInner();
            return _registryInner!.ListBySourceAsync(sourceStreamId, ct);
        }

        public Task<StreamForwardingBinding?> GetAsync(
            string sourceStreamId,
            string targetStreamId,
            CancellationToken ct = default)
        {
            EnsureInner();
            return _authorityInner!.GetAsync(sourceStreamId, targetStreamId, ct);
        }

        public Task<StreamForwardingBinding> WaitForUpsertAsync(
            string sourceStreamId,
            string targetStreamId,
            TimeSpan timeout)
        {
            var key = (sourceStreamId, targetStreamId);
            TaskCompletionSource<StreamForwardingBinding> completion;
            lock (_gate)
            {
                if (_observedUpserts.Contains(key))
                {
                    return Task.FromResult(new StreamForwardingBinding
                    {
                        SourceStreamId = sourceStreamId,
                        TargetStreamId = targetStreamId,
                    });
                }

                completion = GetOrCreateNextUpsertCompletion(key);
            }

            return completion.Task.WaitAsync(timeout);
        }

        private void EnsureInner()
        {
            if (_registryInner == null || _authorityInner == null)
                throw new InvalidOperationException("Inner stream forwarding registry has not been assigned.");
        }

        private TaskCompletionSource<StreamForwardingBinding> GetOrCreateNextUpsertCompletion(
            (string SourceStreamId, string TargetStreamId) key)
        {
            if (!_nextUpsertsByKey.TryGetValue(key, out var completion))
            {
                completion = new TaskCompletionSource<StreamForwardingBinding>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _nextUpsertsByKey[key] = completion;
            }

            return completion;
        }

        private TaskCompletionSource<StreamForwardingBinding> ConsumeNextUpsertCompletion(
            (string SourceStreamId, string TargetStreamId) key)
        {
            var completion = GetOrCreateNextUpsertCompletion(key);
            _nextUpsertsByKey.Remove(key);
            return completion;
        }

        private static StreamForwardingBinding CloneBinding(StreamForwardingBinding binding) =>
            new()
            {
                SourceStreamId = binding.SourceStreamId,
                TargetStreamId = binding.TargetStreamId,
                ForwardingMode = binding.ForwardingMode,
                DirectionFilter = new HashSet<TopologyAudience>(binding.DirectionFilter),
                EventTypeFilter = new HashSet<string>(binding.EventTypeFilter, StringComparer.Ordinal),
                Version = binding.Version,
                LeaseId = binding.LeaseId,
            };
    }

    internal sealed class ObservingExternalIdentityBindingWriter
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, List<BindingWriteObservation>> _observationsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExternalIdentityBindingDocument> _latestUpsertsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<ExternalIdentityBindingDocument>> _nextUpsertsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<string>> _nextDeletesById = new(StringComparer.Ordinal);

        public void ObserveUpsert(ExternalIdentityBindingDocument document)
        {
            TaskCompletionSource<ExternalIdentityBindingDocument> completion;
            lock (_gate)
            {
                ObservationsFor(document.Id).Add(BindingWriteObservation.Upsert(
                    document.Id,
                    document.BindingId,
                    document.StateVersion));
                _latestUpsertsById[document.Id] = document.Clone();
                completion = ConsumeNextUpsertCompletion(document.Id);
            }

            completion.TrySetResult(document.Clone());
        }

        public void ObserveDelete(string id)
        {
            TaskCompletionSource<string> completion;
            lock (_gate)
            {
                ObservationsFor(id).Add(BindingWriteObservation.Delete(id));
                completion = ConsumeNextDeleteCompletion(id);
            }

            completion.TrySetResult(id);
        }

        public Task<ExternalIdentityBindingDocument> WaitForUpsertAsync(string id, TimeSpan timeout)
        {
            TaskCompletionSource<ExternalIdentityBindingDocument> completion;
            lock (_gate)
            {
                if (_latestUpsertsById.TryGetValue(id, out var latest))
                    return Task.FromResult(latest.Clone());
                completion = GetOrCreateNextUpsertCompletion(id);
            }

            return completion.Task.WaitAsync(timeout);
        }

        public Task<string> WaitForDeleteAsync(string id, TimeSpan timeout)
        {
            TaskCompletionSource<string> completion;
            lock (_gate)
            {
                if (_observationsById.TryGetValue(id, out var observations) &&
                    observations.Any(static observation => observation.Operation == nameof(BindingWriteObservation.Delete)))
                {
                    return Task.FromResult(id);
                }

                completion = GetOrCreateNextDeleteCompletion(id);
            }

            return completion.Task.WaitAsync(timeout);
        }

        public IReadOnlyList<BindingWriteObservation> Snapshot(string id)
        {
            lock (_gate)
            {
                return _observationsById.TryGetValue(id, out var observations)
                    ? observations.ToArray()
                    : [];
            }
        }

        private List<BindingWriteObservation> ObservationsFor(string id)
        {
            if (!_observationsById.TryGetValue(id, out var observations))
            {
                observations = new List<BindingWriteObservation>();
                _observationsById[id] = observations;
            }

            return observations;
        }

        private TaskCompletionSource<ExternalIdentityBindingDocument> GetOrCreateNextUpsertCompletion(string id)
        {
            if (!_nextUpsertsById.TryGetValue(id, out var completion))
            {
                completion = new TaskCompletionSource<ExternalIdentityBindingDocument>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _nextUpsertsById[id] = completion;
            }

            return completion;
        }

        private TaskCompletionSource<ExternalIdentityBindingDocument> ConsumeNextUpsertCompletion(string id)
        {
            var completion = GetOrCreateNextUpsertCompletion(id);
            _nextUpsertsById.Remove(id);
            return completion;
        }

        private TaskCompletionSource<string> GetOrCreateNextDeleteCompletion(string id)
        {
            if (!_nextDeletesById.TryGetValue(id, out var completion))
            {
                completion = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _nextDeletesById[id] = completion;
            }

            return completion;
        }

        private TaskCompletionSource<string> ConsumeNextDeleteCompletion(string id)
        {
            var completion = GetOrCreateNextDeleteCompletion(id);
            _nextDeletesById.Remove(id);
            return completion;
        }
    }

    internal sealed record BindingWriteObservation(
        string Id,
        string Operation,
        string? BindingId,
        long? StateVersion)
    {
        public static BindingWriteObservation Upsert(string id, string bindingId, long stateVersion) =>
            new(id, nameof(Upsert), bindingId, stateVersion);

        public static BindingWriteObservation Delete(string id) =>
            new(id, nameof(Delete), null, null);
    }

    internal sealed class ObservingProjectionDocumentWriter(
        IProjectionDocumentWriter<ExternalIdentityBindingDocument> inner,
        ObservingExternalIdentityBindingWriter observer)
        : IProjectionDocumentWriter<ExternalIdentityBindingDocument>
    {
        public async Task<ProjectionWriteResult> UpsertAsync(
            ExternalIdentityBindingDocument readModel,
            CancellationToken ct = default)
        {
            var result = await inner.UpsertAsync(readModel, ct);
            if (result.IsApplied)
                observer.ObserveUpsert(readModel);
            return result;
        }

        public async Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            var result = await inner.DeleteAsync(id, ct);
            if (result.IsApplied)
                observer.ObserveDelete(id);
            return result;
        }
    }
}

internal static class ChannelIdentityOrleansDispatchProjectionTestServiceCollectionExtensions
{
    public static IServiceCollection DecorateStreamForwardingRegistry(
        this IServiceCollection services,
        ChannelIdentityOrleansDispatchProjectionTests.ObservingStreamForwardingRegistry observer)
    {
        var registryDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IStreamForwardingRegistry))
            .ToArray();
        var authorityDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IStreamForwardingBindingAuthority))
            .ToArray();
        registryDescriptors.Should().ContainSingle();
        authorityDescriptors.Should().ContainSingle();

        foreach (var descriptor in registryDescriptors.Concat(authorityDescriptors))
            services.Remove(descriptor);

        services.AddSingleton(observer);
        services.AddSingleton<IStreamForwardingRegistry>(provider =>
        {
            var registryObserver = provider.GetRequiredService<ChannelIdentityOrleansDispatchProjectionTests.ObservingStreamForwardingRegistry>();
            registryObserver.SetInner(
                ResolveOriginalStreamForwardingRegistry(provider, registryDescriptors[0]),
                ResolveOriginalStreamForwardingBindingAuthority(provider, authorityDescriptors[0]));
            return registryObserver;
        });
        services.AddSingleton<IStreamForwardingBindingAuthority>(provider =>
            provider.GetRequiredService<ChannelIdentityOrleansDispatchProjectionTests.ObservingStreamForwardingRegistry>());
        return services;
    }

    public static IServiceCollection DecorateExternalIdentityBindingWriter(
        this IServiceCollection services,
        ChannelIdentityOrleansDispatchProjectionTests.ObservingExternalIdentityBindingWriter observer)
    {
        var descriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IProjectionDocumentWriter<ExternalIdentityBindingDocument>))
            .ToArray();
        descriptors.Should().ContainSingle();

        foreach (var descriptor in descriptors)
            services.Remove(descriptor);

        services.AddSingleton<IProjectionDocumentWriter<ExternalIdentityBindingDocument>>(provider =>
            new ChannelIdentityOrleansDispatchProjectionTests.ObservingProjectionDocumentWriter(
                ResolveOriginalWriter(provider, descriptors[0]),
                observer));
        return services;
    }

    private static IStreamForwardingRegistry ResolveOriginalStreamForwardingRegistry(
        IServiceProvider provider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IStreamForwardingRegistry instance)
            return instance;

        if (descriptor.ImplementationFactory is not null)
            return (IStreamForwardingRegistry)descriptor.ImplementationFactory(provider);

        if (descriptor.ImplementationType is not null)
        {
            return (IStreamForwardingRegistry)ActivatorUtilities.CreateInstance(
                provider,
                descriptor.ImplementationType);
        }

        throw new InvalidOperationException("Unable to resolve original stream forwarding registry.");
    }

    private static IStreamForwardingBindingAuthority ResolveOriginalStreamForwardingBindingAuthority(
        IServiceProvider provider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IStreamForwardingBindingAuthority instance)
            return instance;

        if (descriptor.ImplementationFactory is not null)
            return (IStreamForwardingBindingAuthority)descriptor.ImplementationFactory(provider);

        if (descriptor.ImplementationType is not null)
        {
            return (IStreamForwardingBindingAuthority)ActivatorUtilities.CreateInstance(
                provider,
                descriptor.ImplementationType);
        }

        throw new InvalidOperationException("Unable to resolve original stream forwarding binding authority.");
    }

    private static IProjectionDocumentWriter<ExternalIdentityBindingDocument> ResolveOriginalWriter(
        IServiceProvider provider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IProjectionDocumentWriter<ExternalIdentityBindingDocument> instance)
            return instance;

        if (descriptor.ImplementationFactory is not null)
            return (IProjectionDocumentWriter<ExternalIdentityBindingDocument>)descriptor.ImplementationFactory(provider);

        if (descriptor.ImplementationType is not null)
        {
            return (IProjectionDocumentWriter<ExternalIdentityBindingDocument>)ActivatorUtilities.CreateInstance(
                provider,
                descriptor.ImplementationType);
        }

        throw new InvalidOperationException("Unable to resolve original ExternalIdentityBindingDocument writer.");
    }
}
