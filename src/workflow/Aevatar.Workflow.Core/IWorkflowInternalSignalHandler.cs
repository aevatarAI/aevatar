using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core;

internal interface IWorkflowInternalSignalHandler
{
    bool CanHandle(EventEnvelope envelope);

    Task HandleAsync(EventEnvelope envelope, CancellationToken ct);
}
