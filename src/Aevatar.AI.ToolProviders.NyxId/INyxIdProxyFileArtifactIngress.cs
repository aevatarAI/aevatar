using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.AI.ToolProviders.NyxId;

public interface INyxIdProxyFileArtifactIngress
{
    ValueTask<WorkflowFileIngressResult> IngestAsync(
        WorkflowFileIngressRequest request,
        CancellationToken cancellationToken = default);
}
