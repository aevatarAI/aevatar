using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionLiveSinkForwarder
    : IProjectionPortLiveSinkForwarder<WorkflowExecutionRuntimeLease, IWorkflowRunEventSink, WorkflowRunEvent>;
