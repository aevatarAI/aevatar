using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

// Test-add (06-20-observatory-run-state-feed / R2): the reclaim gate confirms an ad-hoc run's durable
// current-state doc has materialized up to the run's committed head version before its throwaway actors are
// reclaimed. It must DEFER (never confirm) on unknown head version, watermark-unreached (timeout), or an
// absent materialization scope — so the destroy never races ahead of materialization (silent doc loss).
public sealed class WorkflowRunMaterializationReclaimGateTests
{
    [Fact]
    public async Task TryConfirmMaterializedAsync_WhenWatermarkReachesHeadVersion_ShouldConfirm()
    {
        var versionPort = new FakeCommittedVersionPort { Version = 7 };
        var watermarkPort = new FakeMaterializationWatermarkPort { Version = 7 };
        var gate = CreateGate(versionPort, watermarkPort);

        var confirmed = await gate.TryConfirmMaterializedAsync("run-1", CancellationToken.None);

        confirmed.Should().BeTrue();
        watermarkPort.Reads.Should().Be(1);
    }

    [Fact]
    public async Task TryConfirmMaterializedAsync_WhenWatermarkLaggingThenCatchesUp_ShouldConfirmAfterPolling()
    {
        var versionPort = new FakeCommittedVersionPort { Version = 5 };
        // Lagging watermark (3) for the first two reads, then reaches the head version (5).
        var watermarkPort = new FakeMaterializationWatermarkPort
        {
            VersionSequence = [3, 3, 5],
        };
        var gate = CreateGate(versionPort, watermarkPort);

        var confirmed = await gate.TryConfirmMaterializedAsync("run-1", CancellationToken.None);

        confirmed.Should().BeTrue();
        watermarkPort.Reads.Should().Be(3);
    }

    [Fact]
    public async Task TryConfirmMaterializedAsync_WhenWatermarkNeverReachesHeadVersion_ShouldDefer()
    {
        var versionPort = new FakeCommittedVersionPort { Version = 9 };
        var watermarkPort = new FakeMaterializationWatermarkPort { Version = 4 };
        var gate = CreateGate(versionPort, watermarkPort, maxPolls: 3);

        var confirmed = await gate.TryConfirmMaterializedAsync("run-1", CancellationToken.None);

        confirmed.Should().BeFalse();
        watermarkPort.Reads.Should().Be(3);
    }

    [Fact]
    public async Task TryConfirmMaterializedAsync_WhenMaterializationScopeAbsent_ShouldDefer()
    {
        var versionPort = new FakeCommittedVersionPort { Version = 6 };
        // Null watermark models an absent/unreadable materialization status scope.
        var watermarkPort = new FakeMaterializationWatermarkPort { Version = null };
        var gate = CreateGate(versionPort, watermarkPort, maxPolls: 2);

        var confirmed = await gate.TryConfirmMaterializedAsync("run-1", CancellationToken.None);

        confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task TryConfirmMaterializedAsync_WhenHeadVersionUnknown_ShouldDeferWithoutReadingWatermark()
    {
        var versionPort = new FakeCommittedVersionPort { Version = null };
        var watermarkPort = new FakeMaterializationWatermarkPort { Version = 100 };
        var gate = CreateGate(versionPort, watermarkPort);

        var confirmed = await gate.TryConfirmMaterializedAsync("run-1", CancellationToken.None);

        confirmed.Should().BeFalse();
        watermarkPort.Reads.Should().Be(0);
    }

    [Fact]
    public async Task TryConfirmMaterializedAsync_WhenRunActorIdMissing_ShouldDefer()
    {
        var gate = CreateGate(new FakeCommittedVersionPort { Version = 3 }, new FakeMaterializationWatermarkPort { Version = 3 });

        var confirmed = await gate.TryConfirmMaterializedAsync("   ", CancellationToken.None);

        confirmed.Should().BeFalse();
    }

    private static WorkflowRunMaterializationReclaimGate CreateGate(
        FakeCommittedVersionPort versionPort,
        FakeMaterializationWatermarkPort watermarkPort,
        int maxPolls = 5)
    {
        return new WorkflowRunMaterializationReclaimGate(
            versionPort,
            watermarkPort,
            new WorkflowRunReclaimOptions
            {
                MaxWatermarkPolls = maxPolls,
                WatermarkPollInterval = TimeSpan.Zero,
            },
            // Deterministic: never actually delay between polls.
            delayAsync: (_, _) => Task.CompletedTask);
    }

    private sealed class FakeCommittedVersionPort : IWorkflowRunCommittedVersionPort
    {
        public long? Version { get; set; }

        public Task<long?> GetCommittedVersionAsync(string runActorId, CancellationToken ct = default) =>
            Task.FromResult(Version);
    }

    private sealed class FakeMaterializationWatermarkPort : IWorkflowRunMaterializationWatermarkPort
    {
        public long? Version { get; set; }
        public IReadOnlyList<long?>? VersionSequence { get; set; }
        public int Reads { get; private set; }

        public Task<long?> GetMaterializedVersionAsync(string runActorId, CancellationToken ct = default)
        {
            long? value;
            if (VersionSequence is { Count: > 0 })
            {
                var index = Math.Min(Reads, VersionSequence.Count - 1);
                value = VersionSequence[index];
            }
            else
            {
                value = Version;
            }

            Reads++;
            return Task.FromResult(value);
        }
    }
}
