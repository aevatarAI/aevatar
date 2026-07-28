using System.Text;

namespace Aevatar.AI.Abstractions.Prompting;

public sealed record PromptLayerBounds
{
    public PromptLayerBounds(int maxUtf8Bytes, int maxEstimatedTokens)
    {
        if (maxUtf8Bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        if (maxEstimatedTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEstimatedTokens));

        MaxUtf8Bytes = maxUtf8Bytes;
        MaxEstimatedTokens = maxEstimatedTokens;
    }

    public int MaxUtf8Bytes { get; }
    public int MaxEstimatedTokens { get; }
}

public enum PromptLayerDiagnosticCode
{
    ProviderReported = 0,
    OptionalLayerRejectedOverBudget = 1,
    DiagnosticsTruncated = 2,
}

public sealed record PromptLayerDiagnostic(PromptLayerDiagnosticCode Code, string Detail);

public sealed record KernelPromptProvenance(string Source);
public sealed record BuiltInPromptFloorProvenance(string Source);
public sealed record GlobalSystemSkillPromptProvenance(string SourceWatermark);
public sealed record ProfileRoutingPromptProvenance(string Source);
public sealed record SelectedSkillPromptProvenance(string Source);
public sealed record RuntimeFactsPromptProvenance(string Source);
public sealed record ConversationContextPromptProvenance(string SummarySource);

public sealed class KernelPromptLayer
{
    public KernelPromptLayer(
        string content,
        KernelPromptProvenance provenance,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics = null)
    {
        Content = content ?? string.Empty;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Diagnostics = PromptLayerValues.CopyDiagnostics(diagnostics);
        ActualUtf8Bytes = PromptLayerValues.MeasureUtf8Bytes(Content);
        EstimatedTokens = PromptLayerValues.EstimateTokens(ActualUtf8Bytes);
    }

    public string Content { get; }
    public KernelPromptProvenance Provenance { get; }
    public PromptLayerBounds Bounds { get; } = new(16 * 1024, 4096);
    public int ActualUtf8Bytes { get; }
    public int EstimatedTokens { get; }
    public IReadOnlyList<PromptLayerDiagnostic> Diagnostics { get; }
}

public sealed class BuiltInPromptFloorLayer
{
    public BuiltInPromptFloorLayer(
        string content,
        BuiltInPromptFloorProvenance provenance,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics = null)
    {
        Content = content ?? string.Empty;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Diagnostics = PromptLayerValues.CopyDiagnostics(diagnostics);
        ActualUtf8Bytes = PromptLayerValues.MeasureUtf8Bytes(Content);
        EstimatedTokens = PromptLayerValues.EstimateTokens(ActualUtf8Bytes);
    }

    public string Content { get; }
    public BuiltInPromptFloorProvenance Provenance { get; }
    public PromptLayerBounds Bounds { get; } = new(32 * 1024, 8192);
    public int ActualUtf8Bytes { get; }
    public int EstimatedTokens { get; }
    public IReadOnlyList<PromptLayerDiagnostic> Diagnostics { get; }
}

public sealed class GlobalSystemSkillPromptLayer
{
    public GlobalSystemSkillPromptLayer(
        string content,
        GlobalSystemSkillPromptProvenance provenance,
        PromptLayerBounds bounds,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics = null)
    {
        Content = content ?? string.Empty;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        Diagnostics = PromptLayerValues.CopyDiagnostics(diagnostics);
        ActualUtf8Bytes = PromptLayerValues.MeasureUtf8Bytes(Content);
        EstimatedTokens = PromptLayerValues.EstimateTokens(ActualUtf8Bytes);
    }

    public string Content { get; }
    public GlobalSystemSkillPromptProvenance Provenance { get; }
    public PromptLayerBounds Bounds { get; }
    public int ActualUtf8Bytes { get; }
    public int EstimatedTokens { get; }
    public IReadOnlyList<PromptLayerDiagnostic> Diagnostics { get; }
}

public sealed class ProfileRoutingPromptLayer
{
    public ProfileRoutingPromptLayer(
        string content,
        ProfileRoutingPromptProvenance provenance,
        PromptLayerBounds bounds,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics = null)
    {
        Content = content ?? string.Empty;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        Diagnostics = PromptLayerValues.CopyDiagnostics(diagnostics);
        ActualUtf8Bytes = PromptLayerValues.MeasureUtf8Bytes(Content);
        EstimatedTokens = PromptLayerValues.EstimateTokens(ActualUtf8Bytes);
    }

    public string Content { get; }
    public ProfileRoutingPromptProvenance Provenance { get; }
    public PromptLayerBounds Bounds { get; }
    public int ActualUtf8Bytes { get; }
    public int EstimatedTokens { get; }
    public IReadOnlyList<PromptLayerDiagnostic> Diagnostics { get; }
}

public sealed class SelectedSkillPromptLayer
{
    public SelectedSkillPromptLayer(
        string content,
        SelectedSkillPromptProvenance provenance,
        PromptLayerBounds bounds,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics = null)
    {
        Content = content ?? string.Empty;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        Diagnostics = PromptLayerValues.CopyDiagnostics(diagnostics);
        ActualUtf8Bytes = PromptLayerValues.MeasureUtf8Bytes(Content);
        EstimatedTokens = PromptLayerValues.EstimateTokens(ActualUtf8Bytes);
    }

    public string Content { get; }
    public SelectedSkillPromptProvenance Provenance { get; }
    public PromptLayerBounds Bounds { get; }
    public int ActualUtf8Bytes { get; }
    public int EstimatedTokens { get; }
    public IReadOnlyList<PromptLayerDiagnostic> Diagnostics { get; }
}

public sealed class RuntimeFactsPromptLayer
{
    public RuntimeFactsPromptLayer(
        string content,
        RuntimeFactsPromptProvenance provenance,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics = null)
    {
        Content = content ?? string.Empty;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Diagnostics = PromptLayerValues.CopyDiagnostics(diagnostics);
        ActualUtf8Bytes = PromptLayerValues.MeasureUtf8Bytes(Content);
        EstimatedTokens = PromptLayerValues.EstimateTokens(ActualUtf8Bytes);
    }

    public string Content { get; }
    public RuntimeFactsPromptProvenance Provenance { get; }
    public PromptLayerBounds Bounds { get; } = new(16 * 1024, 4096);
    public int ActualUtf8Bytes { get; }
    public int EstimatedTokens { get; }
    public IReadOnlyList<PromptLayerDiagnostic> Diagnostics { get; }
}

public sealed class ConversationContextPromptLayer
{
    public ConversationContextPromptLayer(
        string content,
        ConversationContextPromptProvenance provenance,
        PromptLayerBounds bounds,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics = null)
    {
        Content = content ?? string.Empty;
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        Bounds = bounds ?? throw new ArgumentNullException(nameof(bounds));
        Diagnostics = PromptLayerValues.CopyDiagnostics(diagnostics);
        ActualUtf8Bytes = PromptLayerValues.MeasureUtf8Bytes(Content);
        EstimatedTokens = PromptLayerValues.EstimateTokens(ActualUtf8Bytes);
    }

    public string Content { get; }
    public ConversationContextPromptProvenance Provenance { get; }
    public PromptLayerBounds Bounds { get; }
    public int ActualUtf8Bytes { get; }
    public int EstimatedTokens { get; }
    public IReadOnlyList<PromptLayerDiagnostic> Diagnostics { get; }
}

public sealed class PromptLayerCompositionReport
{
    public PromptLayerCompositionReport(
        bool included,
        int actualUtf8Bytes,
        int estimatedTokens,
        PromptLayerBounds? bounds,
        IReadOnlyList<PromptLayerDiagnostic> diagnostics)
    {
        Included = included;
        ActualUtf8Bytes = actualUtf8Bytes;
        EstimatedTokens = estimatedTokens;
        Bounds = bounds;
        Diagnostics = diagnostics;
    }

    public bool Included { get; }
    public int ActualUtf8Bytes { get; }
    public int EstimatedTokens { get; }
    public PromptLayerBounds? Bounds { get; }
    public IReadOnlyList<PromptLayerDiagnostic> Diagnostics { get; }
}

public sealed class SystemPromptCompositionResult
{
    public SystemPromptCompositionResult(
        string prompt,
        PromptLayerCompositionReport kernel,
        PromptLayerCompositionReport builtInFloor,
        PromptLayerCompositionReport global,
        PromptLayerCompositionReport profile,
        PromptLayerCompositionReport selectedSkill,
        PromptLayerCompositionReport runtimeFacts,
        PromptLayerCompositionReport conversation,
        KernelPromptProvenance kernelProvenance,
        BuiltInPromptFloorProvenance builtInFloorProvenance,
        GlobalSystemSkillPromptProvenance? globalProvenance,
        ProfileRoutingPromptProvenance? profileProvenance,
        SelectedSkillPromptProvenance? selectedSkillProvenance,
        RuntimeFactsPromptProvenance? runtimeFactsProvenance,
        ConversationContextPromptProvenance? conversationProvenance)
    {
        Prompt = prompt;
        Kernel = kernel;
        BuiltInFloor = builtInFloor;
        Global = global;
        Profile = profile;
        SelectedSkill = selectedSkill;
        RuntimeFacts = runtimeFacts;
        Conversation = conversation;
        Reports = [Kernel, BuiltInFloor, Global, Profile, SelectedSkill, RuntimeFacts, Conversation];
        Diagnostics = Reports.SelectMany(static report => report.Diagnostics).ToArray();
        KernelProvenance = kernelProvenance;
        BuiltInFloorProvenance = builtInFloorProvenance;
        GlobalProvenance = globalProvenance;
        ProfileProvenance = profileProvenance;
        SelectedSkillProvenance = selectedSkillProvenance;
        RuntimeFactsProvenance = runtimeFactsProvenance;
        ConversationProvenance = conversationProvenance;
    }

    public string Prompt { get; }
    public PromptLayerCompositionReport Kernel { get; }
    public PromptLayerCompositionReport BuiltInFloor { get; }
    public PromptLayerCompositionReport Global { get; }
    public PromptLayerCompositionReport Profile { get; }
    public PromptLayerCompositionReport SelectedSkill { get; }
    public PromptLayerCompositionReport RuntimeFacts { get; }
    public PromptLayerCompositionReport Conversation { get; }
    public IReadOnlyList<PromptLayerCompositionReport> Reports { get; }
    public IReadOnlyList<PromptLayerDiagnostic> Diagnostics { get; }
    public KernelPromptProvenance KernelProvenance { get; }
    public BuiltInPromptFloorProvenance BuiltInFloorProvenance { get; }
    public GlobalSystemSkillPromptProvenance? GlobalProvenance { get; }
    public ProfileRoutingPromptProvenance? ProfileProvenance { get; }
    public SelectedSkillPromptProvenance? SelectedSkillProvenance { get; }
    public RuntimeFactsPromptProvenance? RuntimeFactsProvenance { get; }
    public ConversationContextPromptProvenance? ConversationProvenance { get; }
}

public sealed class PromptLayerCompositionException : Exception
{
    public PromptLayerCompositionException(string message)
        : base(message)
    {
    }
}

internal static class PromptLayerValues
{
    public static int MeasureUtf8Bytes(string content) => Encoding.UTF8.GetByteCount(content);

    public static int EstimateTokens(int actualUtf8Bytes) => (actualUtf8Bytes + 3) / 4;

    public static IReadOnlyList<PromptLayerDiagnostic> CopyDiagnostics(
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics) =>
        diagnostics is null or { Count: 0 } ? [] : diagnostics.ToArray();
}
