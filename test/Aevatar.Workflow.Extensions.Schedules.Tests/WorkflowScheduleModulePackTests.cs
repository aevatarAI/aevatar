using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Extensions.Schedules;
using Aevatar.Workflow.Extensions.Schedules.Modules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Schedules.Tests;

public sealed class WorkflowScheduleModulePackTests
{
    [Theory]
    [InlineData("self_reschedule")]
    [InlineData("schedule_workflow")]
    public void WorkflowModuleFactory_WhenSchedulePackRegistered_ShouldCreateScheduleModule(string moduleName)
    {
        var services = new ServiceCollection();
        services.AddAevatarWorkflow();
        services.AddWorkflowScheduleExtensions();
        services.AddSingleton<IWorkflowScheduleCommandPort, RecordingWorkflowScheduleCommandPort>();
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IEventModuleFactory<IWorkflowExecutionContext>>();
        var created = factory.TryCreate(moduleName, out var module);

        created.Should().BeTrue();
        module.Should().BeOfType<WorkflowSelfRescheduleModule>();
    }

    [Fact]
    public void AddWorkflowScheduleExtensions_ShouldRegisterExactlyOneScheduleModulePack()
    {
        var services = new ServiceCollection();

        services.AddWorkflowScheduleExtensions();
        services.AddWorkflowScheduleExtensions();

        using var provider = services.BuildServiceProvider();
        var packs = provider.GetServices<IWorkflowModulePack>().ToList();

        packs.Count(x => x is WorkflowScheduleModulePack).Should().Be(1);
    }

    private sealed class RecordingWorkflowScheduleCommandPort : IWorkflowScheduleCommandPort
    {
        public Task<WorkflowScheduleMutationReceipt> EnsureAsync(
            WorkflowScheduleConfiguration configuration,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowScheduleMutationReceipt(
                configuration.ScheduleId,
                $"actor:{configuration.ScheduleId}",
                true,
                "command-1",
                "correlation-1",
                DateTimeOffset.UtcNow,
                "accepted"));
    }
}
