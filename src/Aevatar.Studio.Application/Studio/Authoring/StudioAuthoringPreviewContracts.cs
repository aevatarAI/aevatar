using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.Studio.Application.Studio.Authoring;

// Refactor (iter21/cluster-001):
//   Old pattern: Host services owned Ask AI authoring loops, fake actor ids, process gates, and stream aggregation.
//   New principle: Application owns typed request-scoped preview orchestration; Host only adapts HTTP/SSE frames.
public enum StudioAuthoringKind
{
    Workflow,
    Script,
}

// Refactor (iter21/cluster-001):
//   Old pattern: Host-specific generator DTOs mixed request semantics with endpoint orchestration details.
//   New principle: Application exposes one typed preview request that Host can only adapt.
public sealed record StudioAuthoringPreviewRequest(
    StudioAuthoringKind Kind,
    string Prompt,
    string? CurrentYaml = null,
    IReadOnlyCollection<string>? AvailableWorkflowNames = null,
    string? CurrentSource = null,
    AppScriptPackage? CurrentPackage = null,
    string? CurrentFilePath = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    LLMControlContext? LlmControl = null);

// Refactor (iter21/cluster-001):
//   Old pattern: Host generator progress enums were split by fake workflow/script service shells.
//   New principle: Application exposes one authoring progress vocabulary shared by both preview kinds.
public enum StudioAuthoringProgressStage
{
    Starting,
    GeneratingDraft,
    ValidatingDraft,
    RepairingDraft,
    Completed,
}

// Refactor (iter21/cluster-001):
//   Old pattern: Host stitched reasoning, progress, content, and completion into ad hoc SSE objects.
//   New principle: Application emits typed preview events; Host maps them to existing SSE frames.
public abstract record StudioAuthoringPreviewEvent
{
    public sealed record ReasoningDelta(string Delta) : StudioAuthoringPreviewEvent;

    public sealed record Progress(StudioAuthoringProgressStage Stage, int Attempt, string Message)
        : StudioAuthoringPreviewEvent;

    public sealed record ContentDelta(string Delta) : StudioAuthoringPreviewEvent;

    public sealed record WorkflowCompleted(WorkflowGenerateResult Result) : StudioAuthoringPreviewEvent;

    public sealed record ScriptCompleted(ScriptGenerateResult Result) : StudioAuthoringPreviewEvent;
}

// Refactor (iter21/cluster-001):
//   Old pattern: Host passed fake GAgent Type tokens to choose LLM defaults.
//   New principle: Application passes explicit authoring kind, prompt, request id, and metadata to the outbound port.
public sealed record StudioAuthoringLLMRequest(
    StudioAuthoringKind Kind,
    string Prompt,
    string RequestId,
    IReadOnlyDictionary<string, string>? Metadata,
    LLMControlContext? LlmControl = null);

// Refactor (iter21/cluster-001):
//   Old pattern: Host aggregated provider stream chunks and leaked terminal bookkeeping into endpoint flow.
//   New principle: Application consumes only reasoning/content deltas needed for preview generation.
public sealed record StudioAuthoringLLMChunk(
    string? DeltaContent,
    string? DeltaReasoningContent);

// Refactor (iter21/cluster-001):
//   Old pattern: Workflow generation contracts lived beside Host endpoints and fake actor services.
//   New principle: Workflow authoring preview contracts live in Application with validation results typed.
public sealed record WorkflowGenerateRequest(
    string Prompt,
    string? CurrentYaml,
    IReadOnlyCollection<string>? AvailableWorkflowNames,
    IReadOnlyDictionary<string, string>? Metadata);

// Refactor (iter21/cluster-001):
//   Old pattern: Workflow completion was returned from Host-local generator services.
//   New principle: Application returns a typed workflow preview result for Host SSE adaptation.
public sealed record WorkflowGenerateResult(
    string Yaml,
    int Attempts,
    IReadOnlyList<ValidationFinding> Findings);

// Refactor (iter21/cluster-001):
//   Old pattern: Script generation contracts lived beside Host endpoints and fake actor services.
//   New principle: Script authoring preview contracts live in Application with package/current-file results typed.
public sealed record ScriptGenerateRequest(
    string Prompt,
    string? CurrentSource,
    IReadOnlyDictionary<string, string>? Metadata,
    AppScriptPackage? CurrentPackage = null,
    string? CurrentFilePath = null);

// Refactor (iter21/cluster-001):
//   Old pattern: Script completion was assembled inside Host from JSON and compiler diagnostics.
//   New principle: Application returns a typed script preview result including package/current-file details.
public sealed record ScriptGenerateResult(
    string Source,
    int Attempts,
    IReadOnlyList<string> Diagnostics,
    AppScriptPackage? Package = null,
    string? CurrentFilePath = null);
