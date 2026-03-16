using Aevatar.AI.Abstractions;
using Aevatar.AI.Projection.Reducers;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class AIProjectionReducerCoverageTests
{
    [Fact]
    public void AIProjectionReducers_ShouldRemainConstructible_AsStandaloneComponents()
    {
        var reducers = new object[]
        {
            new TextMessageStartProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
                Array.Empty<IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, TextMessageStartEvent>>()),
            new TextMessageContentProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
                Array.Empty<IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, TextMessageContentEvent>>()),
            new TextMessageEndProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
                Array.Empty<IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, TextMessageEndEvent>>()),
            new ToolCallProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
                Array.Empty<IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, ToolCallEvent>>()),
            new ToolResultProjectionReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>(
                Array.Empty<IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, ToolResultEvent>>()),
        };

        reducers.Should().HaveCount(5);
    }
}
