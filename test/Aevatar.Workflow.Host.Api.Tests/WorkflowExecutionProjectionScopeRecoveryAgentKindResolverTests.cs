using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionProjectionScopeRecoveryAgentKindResolverTests
{
    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_ShouldRegisterExactExecutionMaterializationRecoveryKind()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        var resolver = provider
            .GetServices<IProjectionScopeRecoveryAgentKindResolver>()
            .Should()
            .ContainSingle()
            .Subject;
        var executionScope = new ProjectionRuntimeScopeKey(
            "workflow.definition:wf-alpha:run:run-alpha",
            WorkflowProjectionKinds.ExecutionMaterialization,
            ProjectionRuntimeMode.DurableMaterialization);

        resolver.TryResolve(executionScope, out var agentKind).Should().BeTrue();
        agentKind.Should().Be(WorkflowExecutionMaterializationScopeGAgent.AgentKind);

        resolver.TryResolve(
                executionScope with { ProjectionKind = WorkflowProjectionKinds.Binding },
                out _)
            .Should().BeFalse();
        resolver.TryResolve(
                executionScope with { Mode = ProjectionRuntimeMode.SessionObservation },
                out _)
            .Should().BeFalse();
    }
}
