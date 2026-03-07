using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core;

internal interface IWorkflowResponseHandler
{
    Task<bool> TryHandleAsync(EventEnvelope envelope, string defaultPublisherId, CancellationToken ct);
}
