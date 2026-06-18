using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Mainnet.Host.Api.Hosting;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Capabilities.Tests;

public sealed class ElasticsearchProjectionIndexReconcileHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WhenOneTargetThrows_ShouldReconcileTheRestAndNotThrow()
    {
        var faulted = new RecordingReconcileTarget("alias-faulted", throwOnReconcile: true);
        var healthy1 = new RecordingReconcileTarget("alias-1");
        var healthy2 = new RecordingReconcileTarget("alias-2");

        var provider = new ServiceCollection()
            .AddSingleton<IProjectionIndexReconcileTarget>(faulted)
            .AddSingleton<IProjectionIndexReconcileTarget>(healthy1)
            .AddSingleton<IProjectionIndexReconcileTarget>(healthy2)
            .BuildServiceProvider();

        var sut = new ElasticsearchProjectionIndexReconcileHostedService(
            provider,
            NullLogger<ElasticsearchProjectionIndexReconcileHostedService>.Instance);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        // A single failing target must never crash host boot, and the others must still reconcile.
        await act.Should().NotThrowAsync();
        faulted.ReconcileCalls.Should().Be(1);
        healthy1.ReconcileCalls.Should().Be(1);
        healthy2.ReconcileCalls.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_WhenNoTargets_ShouldBeNoOp()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var sut = new ElasticsearchProjectionIndexReconcileHostedService(
            provider,
            NullLogger<ElasticsearchProjectionIndexReconcileHostedService>.Instance);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private sealed class RecordingReconcileTarget : IProjectionIndexReconcileTarget
    {
        private readonly bool _throwOnReconcile;

        public RecordingReconcileTarget(string indexAlias, bool throwOnReconcile = false)
        {
            IndexAlias = indexAlias;
            _throwOnReconcile = throwOnReconcile;
        }

        public string IndexAlias { get; }

        public int ReconcileCalls { get; private set; }

        public Task ReconcileIndexAsync(CancellationToken ct = default)
        {
            ReconcileCalls++;
            if (_throwOnReconcile)
                throw new InvalidOperationException("boom");

            return Task.CompletedTask;
        }
    }
}
