namespace Aevatar.AI.Abstractions.LLMProviders;

public sealed record LLMProviderCapabilities
{
    public static readonly LLMProviderCapabilities TextOnly = new();

    public IReadOnlySet<ContentPartKind> SupportedInputModalities { get; init; } =
        new HashSet<ContentPartKind> { ContentPartKind.Text };

    public IReadOnlySet<ContentPartKind> SupportedOutputModalities { get; init; } =
        new HashSet<ContentPartKind> { ContentPartKind.Text };

    public bool SupportsStreaming { get; init; } = true;
    public bool SupportsToolCalls { get; init; } = true;
    public bool SupportsReasoningDeltas { get; init; }

    public bool SupportsInput(ContentPartKind kind) =>
        kind == ContentPartKind.Unspecified || SupportedInputModalities.Contains(kind);

    public bool SupportsOutput(ContentPartKind kind) =>
        kind == ContentPartKind.Unspecified || SupportedOutputModalities.Contains(kind);

    public bool SupportsRequest(LLMRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var modality in request.GetRequestedInputModalities())
        {
            if (!SupportsInput(modality))
                return false;
        }

        return true;
    }

    public static LLMProviderCapabilities Merge(LLMProviderCapabilities? primary, LLMProviderCapabilities? secondary)
    {
        if (primary == null)
            return secondary ?? TextOnly;
        if (secondary == null)
            return primary;

        return new LLMProviderCapabilities
        {
            SupportedInputModalities = MergeSets(primary.SupportedInputModalities, secondary.SupportedInputModalities),
            SupportedOutputModalities = MergeSets(primary.SupportedOutputModalities, secondary.SupportedOutputModalities),
            SupportsStreaming = primary.SupportsStreaming || secondary.SupportsStreaming,
            SupportsToolCalls = primary.SupportsToolCalls || secondary.SupportsToolCalls,
            SupportsReasoningDeltas = primary.SupportsReasoningDeltas || secondary.SupportsReasoningDeltas,
        };
    }

    private static IReadOnlySet<ContentPartKind> MergeSets(
        IReadOnlySet<ContentPartKind> left,
        IReadOnlySet<ContentPartKind> right)
    {
        var merged = new HashSet<ContentPartKind>(left);
        merged.UnionWith(right);
        return merged;
    }
}
