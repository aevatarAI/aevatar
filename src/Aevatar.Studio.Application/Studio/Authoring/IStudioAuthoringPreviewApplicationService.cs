namespace Aevatar.Studio.Application.Studio.Authoring;

// Refactor (iter21/cluster-001):
//   Old pattern: Host endpoints directly resolved fake generator services and executed authoring business loops.
//   New principle: Host depends on one Application boundary that returns typed preview events for SSE adaptation.
public interface IStudioAuthoringPreviewApplicationService
{
    IAsyncEnumerable<StudioAuthoringPreviewEvent> PreviewAsync(
        StudioAuthoringPreviewRequest request,
        CancellationToken ct);
}
