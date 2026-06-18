using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdProxyWorkflowFileArtifactIngress(IWorkflowFileIngressPort fileIngress)
    : INyxIdProxyFileArtifactIngress
{
    private readonly IWorkflowFileIngressPort _fileIngress =
        fileIngress ?? throw new ArgumentNullException(nameof(fileIngress));

    public ValueTask<WorkflowFileIngressResult> IngestAsync(
        WorkflowFileIngressRequest request,
        CancellationToken cancellationToken = default) =>
        _fileIngress.IngestAsync(request, cancellationToken);
}
