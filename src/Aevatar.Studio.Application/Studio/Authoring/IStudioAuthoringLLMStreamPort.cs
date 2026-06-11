namespace Aevatar.Studio.Application.Studio.Authoring;

// Refactor (iter21/cluster-001):
//   Old pattern: Host constructed ChatRuntime and aggregated provider stream output inside endpoint-adjacent services.
//   New principle: Application declares a typed outbound stream port; Infrastructure owns ChatRuntime construction.
public interface IStudioAuthoringLLMStreamPort
{
    IAsyncEnumerable<StudioAuthoringLLMChunk> StreamAsync(
        StudioAuthoringLLMRequest request,
        CancellationToken ct);
}
