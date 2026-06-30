using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Extensions.Hosting;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

// Test-add (06-20-observatory-run-state-feed / R2, codex DIFF review §10 C6): the ad-hoc run reclaim gate is
// built from two read ports that the workflow Infrastructure DI ALWAYS registers, so the gate resolves on
// EVERY workflow host. Their underlying dependencies are optional — the durable materialization watermark
// comes from IProjectionScopeWatermarkQueryPort (only wired where the projection-scope status runtime runs)
// and the committed head version from IEventStore (only wired by the persistence runtime). A host lacking
// BOTH must still BOOT and the ports must DEFER (return null head/watermark), so the gate never confirms and
// never destroys throwaway actors on unconfirmed materialization (which would be the silent doc loss R2
// removes).
public sealed class WorkflowRunReclaimGateHostCompositionTests
{
    [Fact]
    public async Task WorkflowCapability_WithoutWatermarkQueryPortOrEventStore_ShouldResolveReclaimPortsThatDefer()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddWorkflowProjectionReadModelProviders(configuration);
        services.AddWorkflowCapability(configuration);

        // The minimal composition intentionally registers neither the watermark query port nor the event store.
        services.Should().NotContain(x => x.ServiceType == typeof(IProjectionScopeWatermarkQueryPort));
        services.Should().NotContain(x => x.ServiceType.Name == "IEventStore");

        await using var provider = services.BuildServiceProvider();

        // Both adapters resolve (Infrastructure always registers them → the gate always resolves on the host).
        var committedVersionPort = provider.GetRequiredService<IWorkflowRunCommittedVersionPort>();
        var watermarkPort = provider.GetRequiredService<IWorkflowRunMaterializationWatermarkPort>();

        committedVersionPort.Should().BeOfType<WorkflowRunCommittedVersionPort>();
        watermarkPort.Should().BeOfType<WorkflowRunMaterializationWatermarkPort>();

        // Absent underlying dependencies → defer (null), so the gate never confirms and never destroys.
        (await committedVersionPort.GetCommittedVersionAsync("scope-workflow:run:run-1", CancellationToken.None))
            .Should().BeNull();
        (await watermarkPort.GetMaterializedVersionAsync("scope-workflow:run:run-1", CancellationToken.None))
            .Should().BeNull();
    }
}
