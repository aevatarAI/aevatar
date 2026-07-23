using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.AI.ToolProviders.NyxId;

public interface INyxIdProxyFileArtifactIngress
{
    ValueTask<FileArtifactIngressResult> IngestAsync(
        FileArtifactIngressRequest request,
        CancellationToken cancellationToken = default);
}
