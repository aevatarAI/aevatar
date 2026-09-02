using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdProxyWorkflowFileArtifactIngress(IFileArtifactIngressPort fileIngress)
    : INyxIdProxyFileArtifactIngress
{
    private readonly IFileArtifactIngressPort _fileIngress =
        fileIngress ?? throw new ArgumentNullException(nameof(fileIngress));

    public ValueTask<FileArtifactIngressResult> IngestAsync(
        FileArtifactIngressRequest request,
        CancellationToken cancellationToken = default) =>
        _fileIngress.IngestAsync(request, cancellationToken);
}
