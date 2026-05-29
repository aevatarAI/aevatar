using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using Aevatar.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class ChannelIdentityOrleansDispatchProjectionTests
{
    [Fact]
    public async Task CommitBindingDispatch_ShouldReplayCommittedEventToChannelIdentityReadModel()
    {
        // Refactor (iter290/cluster517-first): Old: channel identity tests covered handler bodies and in-process dispatch only. New: Orleans dispatch + replay covers committed-state projection.
        var observer = new ObservingExternalIdentityBindingWriter();
        using var host = await StartSiloHostAsync(observer);
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "tenant-issue1313",
            ExternalUserId = $"user-{Guid.NewGuid():N}",
        };
        var actorId = subject.ToActorId();

        var dispatch = host.Services.GetRequiredService<
            ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>();

        var result = await dispatch.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = subject,
            BindingId = "bnd-issue1313-first",
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

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(20);

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
                    services.AddChannelIdentity();
                    services.AddChannelIdentityProjectionStores();
                    services.DecorateExternalIdentityBindingWriter(observer);
                });
            })
            .Build());

    internal sealed class ObservingExternalIdentityBindingWriter
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<ExternalIdentityBindingDocument>> _upsertsById = new(StringComparer.Ordinal);

        public void ObserveUpsert(ExternalIdentityBindingDocument document)
        {
            TaskCompletionSource<ExternalIdentityBindingDocument> completion;
            lock (_gate)
            {
                if (!_upsertsById.TryGetValue(document.Id, out completion!))
                {
                    completion = new TaskCompletionSource<ExternalIdentityBindingDocument>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _upsertsById[document.Id] = completion;
                }
            }

            completion.TrySetResult(document.Clone());
        }

        public Task<ExternalIdentityBindingDocument> WaitForUpsertAsync(string id, TimeSpan timeout)
        {
            TaskCompletionSource<ExternalIdentityBindingDocument> completion;
            lock (_gate)
            {
                if (!_upsertsById.TryGetValue(id, out completion!))
                {
                    completion = new TaskCompletionSource<ExternalIdentityBindingDocument>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _upsertsById[id] = completion;
                }
            }

            return completion.Task.WaitAsync(timeout);
        }
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

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            inner.DeleteAsync(id, ct);
    }
}

internal static class ChannelIdentityOrleansDispatchProjectionTestServiceCollectionExtensions
{
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
