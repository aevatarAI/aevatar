using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRunReducer
{
    public static WorkflowRunState ApplyPatchedEvent(
        WorkflowRunState current,
        WorkflowRunStatePatchedEvent evt) =>
        WorkflowRunStatePatchSupport.ApplyPatch(current, evt);
}
