using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.Studio.Application.Studio.Authoring;

// Refactor (iter21/cluster-001):
//   Old pattern: Host singleton generator services serialized authoring requests and stitched reasoning/content frames.
//   New principle: Application normalizes preview events over a typed stream port; concurrency is request-scoped.
internal sealed class StudioAuthoringPreviewApplicationService : IStudioAuthoringPreviewApplicationService
{
    private readonly IStudioAuthoringLLMStreamPort _llmStreamPort;
    private readonly WorkflowAuthoringPreviewGenerator _workflowGenerator;
    // Nullable by design: the scripting capability is optional. Hosts composed without it
    // have no script generator, and script authoring previews are rejected below.
    private readonly ScriptAuthoringPreviewGenerator? _scriptGenerator;

    public StudioAuthoringPreviewApplicationService(
        IStudioAuthoringLLMStreamPort llmStreamPort,
        WorkflowAuthoringPreviewGenerator workflowGenerator,
        ScriptAuthoringPreviewGenerator? scriptGenerator = null)
    {
        _llmStreamPort = llmStreamPort ?? throw new ArgumentNullException(nameof(llmStreamPort));
        _workflowGenerator = workflowGenerator ?? throw new ArgumentNullException(nameof(workflowGenerator));
        _scriptGenerator = scriptGenerator;
    }

    public async IAsyncEnumerable<StudioAuthoringPreviewEvent> PreviewAsync(
        StudioAuthoringPreviewRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompt = (request.Prompt ?? string.Empty).Trim();
        if (prompt.Length == 0)
            throw new InvalidOperationException(GetPromptRequiredMessage(request.Kind));

        yield return new StudioAuthoringPreviewEvent.Progress(
            StudioAuthoringProgressStage.Starting,
            0,
            GetStartingMessage(request.Kind));

        var events = new List<StudioAuthoringPreviewEvent>();
        switch (request.Kind)
        {
            case StudioAuthoringKind.Workflow:
            {
                var result = await _workflowGenerator.GenerateAsync(
                    new WorkflowGenerateRequest(
                        prompt,
                        request.CurrentYaml,
                        request.AvailableWorkflowNames,
                        request.Metadata),
                    (turnPrompt, metadata, token) => GenerateTurnAsync(
                        StudioAuthoringKind.Workflow,
                        turnPrompt,
                        metadata,
                        request.LlmControl,
                        events,
                        token),
                    (progress, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        events.Add(progress);
                        return Task.CompletedTask;
                    },
                    ct);

                DrainEvents(events, out var drained);
                foreach (var item in drained)
                    yield return item;

                foreach (var chunk in ChunkText(result.Yaml, 320))
                    yield return new StudioAuthoringPreviewEvent.ContentDelta(chunk);

                yield return new StudioAuthoringPreviewEvent.WorkflowCompleted(result);
                break;
            }
            case StudioAuthoringKind.Script:
            {
                if (_scriptGenerator is not { } scriptGenerator)
                {
                    throw new InvalidOperationException(
                        "Scripting capability is not enabled on this host; script authoring preview is unavailable.");
                }

                var result = await scriptGenerator.GenerateAsync(
                    new ScriptGenerateRequest(
                        prompt,
                        request.CurrentSource,
                        request.Metadata,
                        request.CurrentPackage,
                        request.CurrentFilePath),
                    (turnPrompt, metadata, token) => GenerateTurnAsync(
                        StudioAuthoringKind.Script,
                        turnPrompt,
                        metadata,
                        request.LlmControl,
                        events,
                        token),
                    (progress, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        events.Add(progress);
                        return Task.CompletedTask;
                    },
                    ct);

                DrainEvents(events, out var drained);
                foreach (var item in drained)
                    yield return item;

                foreach (var chunk in ChunkText(result.Source, 320))
                    yield return new StudioAuthoringPreviewEvent.ContentDelta(chunk);

                yield return new StudioAuthoringPreviewEvent.ScriptCompleted(result);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported authoring preview kind: {request.Kind}.");
        }
    }

    private async Task<string?> GenerateTurnAsync(
        StudioAuthoringKind kind,
        string prompt,
        IReadOnlyDictionary<string, string>? metadata,
        LLMControlContext? llmControl,
        List<StudioAuthoringPreviewEvent> events,
        CancellationToken ct)
    {
        var content = new StringBuilder();
        await foreach (var chunk in _llmStreamPort.StreamAsync(
                           new StudioAuthoringLLMRequest(kind, prompt, BuildRequestId(), metadata, llmControl),
                           ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                content.Append(chunk.DeltaContent);

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
                events.Add(new StudioAuthoringPreviewEvent.ReasoningDelta(chunk.DeltaReasoningContent));
        }

        return content.ToString();
    }

    private static string BuildRequestId() => Guid.NewGuid().ToString("N");

    private static void DrainEvents(
        List<StudioAuthoringPreviewEvent> source,
        out StudioAuthoringPreviewEvent[] drained)
    {
        drained = source.ToArray();
        source.Clear();
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var size = chunkSize > 0 ? chunkSize : 320;
        for (var index = 0; index < text.Length; index += size)
        {
            var length = Math.Min(size, text.Length - index);
            yield return text.Substring(index, length);
        }
    }

    private static string GetPromptRequiredMessage(StudioAuthoringKind kind) =>
        kind == StudioAuthoringKind.Script
            ? "Script authoring prompt is required."
            : "Workflow authoring prompt is required.";

    private static string GetStartingMessage(StudioAuthoringKind kind) =>
        kind == StudioAuthoringKind.Script
            ? "Starting Ask AI script generation..."
            : "Starting Ask AI workflow generation...";
}
