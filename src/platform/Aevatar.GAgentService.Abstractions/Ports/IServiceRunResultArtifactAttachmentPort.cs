using Aevatar.ContentArtifacts.Abstractions;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceRunResultArtifactAttachmentPort
{
    Task<ServiceRunArtifactAttachmentResult> AttachResultArtifactsAsync(
        string runActorId,
        string runId,
        long expectedStateVersion,
        IReadOnlyList<ContentArtifactReference> resultArtifacts,
        CancellationToken ct = default);
}

public sealed record ServiceRunArtifactAttachmentResult(
    string RunId,
    string CommandId,
    string CorrelationId,
    DateTimeOffset? AcceptedAtUtc = null);
