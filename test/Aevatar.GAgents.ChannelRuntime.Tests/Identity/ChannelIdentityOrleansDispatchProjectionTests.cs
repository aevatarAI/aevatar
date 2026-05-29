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
        using var scenario = await StartOrleansProjectionScenarioAsync("tenant-issue1313");

        var result = await scenario.CommitDispatch.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = scenario.Subject,
            BindingId = "bnd-issue1313-first",
        });

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().NotBeNull();
        result.Receipt!.ActorId.Should().Be(scenario.ActorId);

        var projected = await scenario.Observer.WaitForUpsertAsync(scenario.ActorId, TestTimeout);

        projected.Id.Should().Be(scenario.ActorId);
        projected.ActorId.Should().Be(scenario.ActorId);
        projected.BindingId.Should().Be("bnd-issue1313-first");
        projected.IsActive.Should().BeTrue();
        projected.ExternalSubject.Should().BeEquivalentTo(scenario.Subject);
        projected.StateVersion.Should().Be(1);
        projected.LastEventId.Should().NotBeNullOrWhiteSpace();

        var reader = scenario.Host.Services.GetRequiredService<IProjectionDocumentReader<ExternalIdentityBindingDocument, string>>();
        var stored = await reader.GetAsync(scenario.ActorId);
        stored.Should().BeEquivalentTo(projected);
    }

    [Fact]
    public async Task RevokeBindingDispatch_ShouldDeleteProjectionAndQueryReturnsNull()
    {
        // Refactor (iter290/cluster517-first): Old: revoke tests stopped at actor/in-process helpers. New: Orleans dispatch must publish a committed delete into the same projection/query path.
        using var scenario = await StartOrleansProjectionScenarioAsync("tenant-issue1343-revoke");

        var commitResult = await scenario.CommitDispatch.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = scenario.Subject,
            BindingId = "bnd-issue1343-revoke",
        });

        commitResult.Succeeded.Should().BeTrue();
        var committed = await scenario.Observer.WaitForUpsertAsync(scenario.ActorId, TestTimeout);
        committed.BindingId.Should().Be("bnd-issue1343-revoke");

        var revokeResult = await scenario.RevokeDispatch.DispatchAsync(new RevokeBindingCommand
        {
            ExternalSubject = scenario.Subject,
            Reason = "user_unbind",
        });

        revokeResult.Succeeded.Should().BeTrue();
        await scenario.Observer.WaitForDeleteAsync(scenario.ActorId, TestTimeout);

        var reader = scenario.Host.Services.GetRequiredService<IProjectionDocumentReader<ExternalIdentityBindingDocument, string>>();
        var stored = await reader.GetAsync(scenario.ActorId);
        stored.Should().BeNull();

        var query = scenario.Host.Services.GetRequiredService<IExternalIdentityBindingQueryPort>();
        var resolved = await query.ResolveAsync(scenario.Subject);
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task DuplicateCommitDispatch_ShouldKeepFirstBindingAndNotProduceSecondEffectiveUpsert()
    {
        // Refactor (iter290/cluster517-first): Old: duplicate commit coverage did not prove Orleans dispatch leaves projection at the first committed binding. New: an observed revoke barrier verifies no second effective upsert was applied.
        using var scenario = await StartOrleansProjectionScenarioAsync("tenant-issue1343-duplicate");

        var firstResult = await scenario.CommitDispatch.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = scenario.Subject,
            BindingId = "bnd-issue1343-first",
        });

        firstResult.Succeeded.Should().BeTrue();
        var firstProjection = await scenario.Observer.WaitForUpsertAsync(scenario.ActorId, TestTimeout);
        firstProjection.BindingId.Should().Be("bnd-issue1343-first");
        firstProjection.StateVersion.Should().Be(1);

        var duplicateResult = await scenario.CommitDispatch.DispatchAsync(new CommitBindingCommand
        {
            ExternalSubject = scenario.Subject,
            BindingId = "bnd-issue1343-second",
        });

        duplicateResult.Succeeded.Should().BeTrue();

        var revokeResult = await scenario.RevokeDispatch.DispatchAsync(new RevokeBindingCommand
        {
            ExternalSubject = scenario.Subject,
            Reason = "duplicate-test-barrier",
        });

        revokeResult.Succeeded.Should().BeTrue();
        await scenario.Observer.WaitForDeleteAsync(scenario.ActorId, TestTimeout);

        var upserts = scenario.Observer.GetAppliedUpserts(scenario.ActorId);
        upserts.Should().ContainSingle();
        upserts[0].BindingId.Should().Be("bnd-issue1343-first");
        upserts[0].StateVersion.Should().Be(1);
    }

    private static TimeSpan TestTimeout => TimeSpan.FromSeconds(20);

    private static async Task<OrleansProjectionScenario> StartOrleansProjectionScenarioAsync(string tenant)
    {
        var observer = new ObservingExternalIdentityBindingWriter();
        var host = await StartSiloHostAsync(observer);
        var subject = new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = tenant,
            ExternalUserId = $"user-{Guid.NewGuid():N}",
        };

        return new OrleansProjectionScenario(
            host,
            observer,
            subject,
            subject.ToActorId(),
            host.Services.GetRequiredService<
                ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>(),
            host.Services.GetRequiredService<
                ICommandDispatchService<RevokeBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>());
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
                    services.AddChannelIdentity();
                    services.AddChannelIdentityProjectionStores();
                    services.DecorateExternalIdentityBindingWriter(observer);
                });
            })
            .Build());

    private sealed record OrleansProjectionScenario(
        IHost Host,
        ObservingExternalIdentityBindingWriter Observer,
        ExternalSubjectRef Subject,
        string ActorId,
        ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> CommitDispatch,
        ICommandDispatchService<RevokeBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> RevokeDispatch)
        : IDisposable
    {
        public void Dispose() => Host.Dispose();
    }

    internal sealed class ObservingExternalIdentityBindingWriter
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<ExternalIdentityBindingDocument>> _upsertsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ExternalIdentityBindingDocument>> _appliedUpsertsById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<string>> _deletesById = new(StringComparer.Ordinal);

        public void ObserveUpsert(ExternalIdentityBindingDocument document)
        {
            // Refactor (iter290/cluster517-first): Old: tests waited only for the first projection write. New: duplicate coverage records every applied upsert without becoming a production read model.
            TaskCompletionSource<ExternalIdentityBindingDocument> completion;
            lock (_gate)
            {
                if (!_appliedUpsertsById.TryGetValue(document.Id, out var upserts))
                {
                    upserts = [];
                    _appliedUpsertsById[document.Id] = upserts;
                }

                upserts.Add(document.Clone());

                if (!_upsertsById.TryGetValue(document.Id, out completion!))
                {
                    completion = new TaskCompletionSource<ExternalIdentityBindingDocument>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _upsertsById[document.Id] = completion;
                }
            }

            completion.TrySetResult(document.Clone());
        }

        public void ObserveDelete(string id)
        {
            // Refactor (iter290/cluster517-first): Old: revoke coverage had no deterministic projection delete signal. New: the test observer exposes the applied delete as a TaskCompletionSource barrier.
            TaskCompletionSource<string> completion;
            lock (_gate)
            {
                if (!_deletesById.TryGetValue(id, out completion!))
                {
                    completion = new TaskCompletionSource<string>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _deletesById[id] = completion;
                }
            }

            completion.TrySetResult(id);
        }

        public Task<ExternalIdentityBindingDocument> WaitForUpsertAsync(string id, TimeSpan timeout)
        {
            // Refactor (iter290/cluster517-first): Old: tests relied on direct handler completion. New: Orleans projection facts wait for observed committed-state materialization.
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

        public Task<string> WaitForDeleteAsync(string id, TimeSpan timeout)
        {
            // Refactor (iter290/cluster517-first): Old: revoke/delete assertions could race the asynchronous projector. New: the test waits for the writer-applied delete event, not a pacing delay.
            TaskCompletionSource<string> completion;
            lock (_gate)
            {
                if (!_deletesById.TryGetValue(id, out completion!))
                {
                    completion = new TaskCompletionSource<string>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _deletesById[id] = completion;
                }
            }

            return completion.Task.WaitAsync(timeout);
        }

        public IReadOnlyList<ExternalIdentityBindingDocument> GetAppliedUpserts(string id)
        {
            // Refactor (iter290/cluster517-first): Old: duplicate commit tests could only inspect final storage. New: applied upsert history proves no second effective projection write occurred.
            lock (_gate)
            {
                return _appliedUpsertsById.TryGetValue(id, out var upserts)
                    ? upserts.Select(static document => document.Clone()).ToArray()
                    : [];
            }
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

        public async Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            // Refactor (iter290/cluster517-first): Old: decorated writer observed only upserts. New: it also observes applied deletes so revoke tests stay event-driven.
            var result = await inner.DeleteAsync(id, ct);
            if (result.IsApplied)
                observer.ObserveDelete(id);
            return result;
        }
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
