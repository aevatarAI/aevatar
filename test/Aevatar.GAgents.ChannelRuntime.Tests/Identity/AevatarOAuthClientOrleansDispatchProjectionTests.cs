using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class AevatarOAuthClientOrleansDispatchProjectionTests
{
    [Fact]
    public async Task ProvisionDispatch_ShouldReplayCommittedEventToReadModel()
    {
        var forwardingObserver = new ObservingStreamForwardingRegistry();
        var documentObserver = new ObservingAevatarOAuthClientWriter();
        using var host = await StartSiloHostAsync(forwardingObserver, documentObserver);
        await EnsureProjectionReadyAsync(host, forwardingObserver);

        var dispatch = host.Services.GetRequiredService<
            ICommandDispatchService<ProvisionAevatarOAuthClientCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>();

        var result = await dispatch.DispatchAsync(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "configured-client",
            ClientIdIssuedAtUnix = 1_700_000_000,
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://api.test/api/oauth/nyxid-callback",
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        });

        result.Succeeded.Should().BeTrue();
        var document = await documentObserver.WaitForProvisionedClientWithKeyAsync(
            AevatarOAuthClientGAgent.WellKnownId,
            TestTimeout);

        document.Id.Should().Be(AevatarOAuthClientGAgent.WellKnownId);
        document.ClientId.Should().Be("configured-client");
        document.IsProvisioned.Should().BeTrue();
        document.HmacKeyRef.Should().NotBeNull();
        document.StateVersion.Should().Be(2);
    }

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(20);

    private static async Task EnsureProjectionReadyAsync(
        IHost host,
        ObservingStreamForwardingRegistry forwardingObserver)
    {
        var targetActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            AevatarOAuthClientGAgent.WellKnownId,
            ChannelIdentityCommittedStateProjectionActivationPlanProvider.AevatarOAuthClientProjectionKind,
            ProjectionRuntimeMode.DurableMaterialization));
        var relayReady = forwardingObserver.WaitForUpsertAsync(
            AevatarOAuthClientGAgent.WellKnownId,
            targetActorId,
            TestTimeout);
        var activation = host.Services.GetRequiredService<
            IProjectionScopeActivationService<AevatarOAuthClientMaterializationRuntimeLease>>();
        await activation.EnsureAsync(new ProjectionScopeStartRequest
        {
            RootActorId = AevatarOAuthClientGAgent.WellKnownId,
            ProjectionKind = ChannelIdentityCommittedStateProjectionActivationPlanProvider.AevatarOAuthClientProjectionKind,
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        });

        await relayReady;
    }

    private static Task<IHost> StartSiloHostAsync(
        ObservingStreamForwardingRegistry forwardingObserver,
        ObservingAevatarOAuthClientWriter documentObserver) =>
        SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    siloPort: ports.SiloPort,
                    gatewayPort: ports.GatewayPort,
                    serviceId: $"aevatar-oauth-client-dispatch-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-oauth-client-dispatch-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                {
                    var configuration = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            [$"{AevatarOAuthClientBootstrapOptions.SectionName}:Enabled"] = "false",
                            [AevatarOAuthClientOptions.ClientIdConfigurationKey] = "configured-client",
                        })
                        .Build();
                    services.AddChannelIdentity(configuration);
                    services.AddSingleton<ISecretVault, InMemorySecretVault>();
                    services.AddInMemoryDocumentProjectionStore<AevatarOAuthClientDocument, string>(
                        static document => document.Id,
                        static key => key);
                    services.DecorateStreamForwardingRegistry(forwardingObserver);
                    services.DecorateAevatarOAuthClientWriter(documentObserver);
                });
            })
            .Build());

    internal sealed class ObservingStreamForwardingRegistry : IStreamForwardingRegistry
    {
        private readonly object _gate = new();
        private readonly HashSet<(string SourceStreamId, string TargetStreamId)> _observedUpserts = [];
        private readonly Dictionary<(string SourceStreamId, string TargetStreamId), TaskCompletionSource<StreamForwardingBinding>>
            _nextUpsertsByKey = new();
        private IStreamForwardingRegistry? _inner;

        public void SetInner(IStreamForwardingRegistry inner) =>
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public async Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            EnsureInner();
            await _inner!.UpsertAsync(binding, ct);

            TaskCompletionSource<StreamForwardingBinding> completion;
            var key = (binding.SourceStreamId, binding.TargetStreamId);
            lock (_gate)
            {
                _observedUpserts.Add(key);
                completion = ConsumeNextUpsertCompletion(key);
            }

            completion.TrySetResult(binding);
        }

        public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
        {
            EnsureInner();
            return _inner!.RemoveAsync(sourceStreamId, targetStreamId, ct);
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(
            string sourceStreamId,
            CancellationToken ct = default)
        {
            EnsureInner();
            return _inner!.ListBySourceAsync(sourceStreamId, ct);
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
                    return Task.FromResult(new StreamForwardingBinding
                    {
                        SourceStreamId = sourceStreamId,
                        TargetStreamId = targetStreamId,
                    });

                completion = GetOrCreateNextUpsertCompletion(key);
            }

            return completion.Task.WaitAsync(timeout);
        }

        private void EnsureInner()
        {
            if (_inner == null)
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
    }

    internal sealed class ObservingAevatarOAuthClientWriter
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, AevatarOAuthClientDocument> _latestUpsertsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<AevatarOAuthClientDocument>> _nextUpsertsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<AevatarOAuthClientDocument>> _nextProvisionedKeyUpsertsById = new(StringComparer.Ordinal);

        public void ObserveUpsert(AevatarOAuthClientDocument document)
        {
            TaskCompletionSource<AevatarOAuthClientDocument> completion;
            TaskCompletionSource<AevatarOAuthClientDocument>? provisionedKeyCompletion = null;
            var documentSnapshot = document.Clone();
            lock (_gate)
            {
                _latestUpsertsById[document.Id] = documentSnapshot;
                completion = ConsumeNextUpsertCompletion(document.Id);
                if (IsProvisionedClientWithKey(documentSnapshot) &&
                    _nextProvisionedKeyUpsertsById.Remove(document.Id, out var keyedCompletion))
                {
                    provisionedKeyCompletion = keyedCompletion;
                }
            }

            completion.TrySetResult(documentSnapshot.Clone());
            provisionedKeyCompletion?.TrySetResult(documentSnapshot.Clone());
        }

        public Task<AevatarOAuthClientDocument> WaitForUpsertAsync(string id, TimeSpan timeout)
        {
            TaskCompletionSource<AevatarOAuthClientDocument> completion;
            lock (_gate)
            {
                if (_latestUpsertsById.TryGetValue(id, out var latest))
                    return Task.FromResult(latest.Clone());
                completion = GetOrCreateNextUpsertCompletion(id);
            }

            return completion.Task.WaitAsync(timeout);
        }

        public Task<AevatarOAuthClientDocument> WaitForProvisionedClientWithKeyAsync(string id, TimeSpan timeout)
        {
            TaskCompletionSource<AevatarOAuthClientDocument> completion;
            lock (_gate)
            {
                if (_latestUpsertsById.TryGetValue(id, out var latest) && IsProvisionedClientWithKey(latest))
                    return Task.FromResult(latest.Clone());

                completion = GetOrCreateNextProvisionedKeyUpsertCompletion(id);
            }

            return completion.Task.WaitAsync(timeout);
        }

        private static bool IsProvisionedClientWithKey(AevatarOAuthClientDocument document) =>
            document.IsProvisioned &&
            document.StateVersion >= 2 &&
            (document.HmacKeyRef != null || !document.HmacKey.IsEmpty);

        private TaskCompletionSource<AevatarOAuthClientDocument> GetOrCreateNextUpsertCompletion(string id)
        {
            if (!_nextUpsertsById.TryGetValue(id, out var completion))
            {
                completion = new TaskCompletionSource<AevatarOAuthClientDocument>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _nextUpsertsById[id] = completion;
            }

            return completion;
        }

        private TaskCompletionSource<AevatarOAuthClientDocument> GetOrCreateNextProvisionedKeyUpsertCompletion(string id)
        {
            if (!_nextProvisionedKeyUpsertsById.TryGetValue(id, out var completion))
            {
                completion = new TaskCompletionSource<AevatarOAuthClientDocument>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _nextProvisionedKeyUpsertsById[id] = completion;
            }

            return completion;
        }

        private TaskCompletionSource<AevatarOAuthClientDocument> ConsumeNextUpsertCompletion(string id)
        {
            var completion = GetOrCreateNextUpsertCompletion(id);
            _nextUpsertsById.Remove(id);
            return completion;
        }
    }

    internal sealed class ObservingAevatarOAuthClientProjectionDocumentWriter(
        IProjectionDocumentWriter<AevatarOAuthClientDocument> inner,
        ObservingAevatarOAuthClientWriter observer)
        : IProjectionDocumentWriter<AevatarOAuthClientDocument>
    {
        public async Task<ProjectionWriteResult> UpsertAsync(
            AevatarOAuthClientDocument readModel,
            CancellationToken ct = default)
        {
            var result = await inner.UpsertAsync(readModel, ct);
            if (result.IsApplied)
                observer.ObserveUpsert(readModel);
            return result;
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            inner.DeleteAsync(id, ct);
    }

    private static IProjectionDocumentWriter<AevatarOAuthClientDocument> ResolveOriginalWriter(
        IServiceProvider provider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IProjectionDocumentWriter<AevatarOAuthClientDocument> instance)
            return instance;

        if (descriptor.ImplementationFactory is not null)
            return (IProjectionDocumentWriter<AevatarOAuthClientDocument>)descriptor.ImplementationFactory(provider);

        if (descriptor.ImplementationType is not null)
        {
            return (IProjectionDocumentWriter<AevatarOAuthClientDocument>)ActivatorUtilities.CreateInstance(
                provider,
                descriptor.ImplementationType);
        }

        throw new InvalidOperationException("Unable to resolve original AevatarOAuthClientDocument writer.");
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
}

internal static class AevatarOAuthClientOrleansDispatchProjectionTestServiceCollectionExtensions
{
    public static IServiceCollection DecorateStreamForwardingRegistry(
        this IServiceCollection services,
        AevatarOAuthClientOrleansDispatchProjectionTests.ObservingStreamForwardingRegistry observer)
    {
        var descriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IStreamForwardingRegistry))
            .ToArray();
        descriptors.Should().ContainSingle();

        foreach (var descriptor in descriptors)
            services.Remove(descriptor);

        services.AddSingleton<IStreamForwardingRegistry>(provider =>
        {
            observer.SetInner(ResolveOriginalStreamForwardingRegistry(provider, descriptors[0]));
            return observer;
        });
        return services;
    }

    public static IServiceCollection DecorateAevatarOAuthClientWriter(
        this IServiceCollection services,
        AevatarOAuthClientOrleansDispatchProjectionTests.ObservingAevatarOAuthClientWriter observer)
    {
        var descriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IProjectionDocumentWriter<AevatarOAuthClientDocument>))
            .ToArray();
        descriptors.Should().ContainSingle();

        foreach (var descriptor in descriptors)
            services.Remove(descriptor);

        services.AddSingleton<IProjectionDocumentWriter<AevatarOAuthClientDocument>>(provider =>
            new AevatarOAuthClientOrleansDispatchProjectionTests.ObservingAevatarOAuthClientProjectionDocumentWriter(
                ResolveOriginalWriter(provider, descriptors[0]),
                observer));
        return services;
    }

    private static IProjectionDocumentWriter<AevatarOAuthClientDocument> ResolveOriginalWriter(
        IServiceProvider provider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IProjectionDocumentWriter<AevatarOAuthClientDocument> instance)
            return instance;

        if (descriptor.ImplementationFactory is not null)
            return (IProjectionDocumentWriter<AevatarOAuthClientDocument>)descriptor.ImplementationFactory(provider);

        if (descriptor.ImplementationType is not null)
        {
            return (IProjectionDocumentWriter<AevatarOAuthClientDocument>)ActivatorUtilities.CreateInstance(
                provider,
                descriptor.ImplementationType);
        }

        throw new InvalidOperationException("Unable to resolve original AevatarOAuthClientDocument writer.");
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
}
