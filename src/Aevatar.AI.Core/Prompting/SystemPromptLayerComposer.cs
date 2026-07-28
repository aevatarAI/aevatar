using System.Text;
using Aevatar.AI.Abstractions.Prompting;

namespace Aevatar.AI.Core.Prompting;

public static class SystemPromptLayerComposer
{
    private const int MaxDiagnosticsPerLayer = 4;
    private const int MaxDiagnosticDetailUtf8Bytes = 256;

    public static SystemPromptCompositionResult Compose(
        KernelPromptLayer kernel,
        BuiltInPromptFloorLayer builtInFloor,
        GlobalSystemSkillPromptLayer? global,
        ProfileRoutingPromptLayer? profile,
        SelectedSkillPromptLayer? selectedSkill,
        RuntimeFactsPromptLayer? runtimeFacts,
        ConversationContextPromptLayer? conversation)
    {
        ValidateRequired(kernel, nameof(kernel));
        ValidateRequired(builtInFloor, nameof(builtInFloor));

        var builder = new StringBuilder();
        Append(builder, kernel.Content);
        Append(builder, builtInFloor.Content);

        var kernelReport = RequiredReport(kernel.ActualUtf8Bytes, kernel.EstimatedTokens, kernel.Bounds, kernel.Diagnostics);
        var builtInFloorReport = RequiredReport(
            builtInFloor.ActualUtf8Bytes,
            builtInFloor.EstimatedTokens,
            builtInFloor.Bounds,
            builtInFloor.Diagnostics);
        var globalReport = AppendOptional(
            builder,
            global?.Content,
            global?.ActualUtf8Bytes ?? 0,
            global?.EstimatedTokens ?? 0,
            global?.Bounds,
            global?.Diagnostics);
        var profileReport = AppendOptional(
            builder,
            profile?.Content,
            profile?.ActualUtf8Bytes ?? 0,
            profile?.EstimatedTokens ?? 0,
            profile?.Bounds,
            profile?.Diagnostics);
        var selectedSkillReport = AppendOptional(
            builder,
            selectedSkill?.Content,
            selectedSkill?.ActualUtf8Bytes ?? 0,
            selectedSkill?.EstimatedTokens ?? 0,
            selectedSkill?.Bounds,
            selectedSkill?.Diagnostics,
            "selected-skill-procedure");
        var runtimeFactsReport = AppendOptional(
            builder,
            runtimeFacts?.Content,
            runtimeFacts?.ActualUtf8Bytes ?? 0,
            runtimeFacts?.EstimatedTokens ?? 0,
            runtimeFacts?.Bounds,
            runtimeFacts?.Diagnostics,
            "untrusted-runtime-facts");
        var conversationReport = AppendOptional(
            builder,
            conversation?.Content,
            conversation?.ActualUtf8Bytes ?? 0,
            conversation?.EstimatedTokens ?? 0,
            conversation?.Bounds,
            conversation?.Diagnostics,
            "untrusted-conversation-summary");

        return new SystemPromptCompositionResult(
            builder.ToString(),
            kernelReport,
            builtInFloorReport,
            globalReport,
            profileReport,
            selectedSkillReport,
            runtimeFactsReport,
            conversationReport,
            kernel.Provenance,
            builtInFloor.Provenance,
            global?.Provenance,
            profile?.Provenance,
            selectedSkill?.Provenance,
            runtimeFacts?.Provenance,
            conversation?.Provenance);
    }

    private static void ValidateRequired(KernelPromptLayer? layer, string parameterName)
    {
        if (layer is null || string.IsNullOrWhiteSpace(layer.Content))
            throw new PromptLayerCompositionException($"Required prompt layer '{parameterName}' is missing or empty.");
        if (IsOverBudget(layer.ActualUtf8Bytes, layer.EstimatedTokens, layer.Bounds))
            throw new PromptLayerCompositionException($"Required prompt layer '{parameterName}' exceeds its declared bounds.");
    }

    private static void ValidateRequired(BuiltInPromptFloorLayer? layer, string parameterName)
    {
        if (layer is null || string.IsNullOrWhiteSpace(layer.Content))
            throw new PromptLayerCompositionException($"Required prompt layer '{parameterName}' is missing or empty.");
        if (IsOverBudget(layer.ActualUtf8Bytes, layer.EstimatedTokens, layer.Bounds))
            throw new PromptLayerCompositionException($"Required prompt layer '{parameterName}' exceeds its declared bounds.");
    }

    private static PromptLayerCompositionReport RequiredReport(
        int actualUtf8Bytes,
        int estimatedTokens,
        PromptLayerBounds bounds,
        IReadOnlyList<PromptLayerDiagnostic> diagnostics) =>
        new(
            included: true,
            actualUtf8Bytes,
            estimatedTokens,
            bounds,
            BoundDiagnostics(composerDiagnostic: null, diagnostics));

    private static PromptLayerCompositionReport AppendOptional(
        StringBuilder builder,
        string? content,
        int actualUtf8Bytes,
        int estimatedTokens,
        PromptLayerBounds? bounds,
        IReadOnlyList<PromptLayerDiagnostic>? diagnostics,
        string? delimiter = null)
    {
        if (bounds is null)
        {
            return new PromptLayerCompositionReport(
                included: false,
                actualUtf8Bytes: 0,
                estimatedTokens: 0,
                bounds: null,
                diagnostics: []);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return new PromptLayerCompositionReport(
                included: false,
                actualUtf8Bytes,
                estimatedTokens,
                bounds,
                BoundDiagnostics(composerDiagnostic: null, diagnostics ?? []));
        }

        if (IsOverBudget(actualUtf8Bytes, estimatedTokens, bounds))
        {
            var rejected = new PromptLayerDiagnostic(
                PromptLayerDiagnosticCode.OptionalLayerRejectedOverBudget,
                $"actual_utf8_bytes={actualUtf8Bytes};max_utf8_bytes={bounds.MaxUtf8Bytes};" +
                $"estimated_tokens={estimatedTokens};max_estimated_tokens={bounds.MaxEstimatedTokens}");
            return new PromptLayerCompositionReport(
                included: false,
                actualUtf8Bytes,
                estimatedTokens,
                bounds,
                BoundDiagnostics(rejected, diagnostics ?? []));
        }

        Append(builder, delimiter is null ? content : Wrap(delimiter, content));
        return new PromptLayerCompositionReport(
            included: true,
            actualUtf8Bytes,
            estimatedTokens,
            bounds,
            BoundDiagnostics(composerDiagnostic: null, diagnostics ?? []));
    }

    private static bool IsOverBudget(int actualUtf8Bytes, int estimatedTokens, PromptLayerBounds bounds) =>
        actualUtf8Bytes > bounds.MaxUtf8Bytes || estimatedTokens > bounds.MaxEstimatedTokens;

    private static IReadOnlyList<PromptLayerDiagnostic> BoundDiagnostics(
        PromptLayerDiagnostic? composerDiagnostic,
        IReadOnlyList<PromptLayerDiagnostic> providerDiagnostics)
    {
        var candidates = new List<PromptLayerDiagnostic>(providerDiagnostics.Count + 1);
        if (composerDiagnostic is not null)
            candidates.Add(composerDiagnostic);
        candidates.AddRange(providerDiagnostics);

        if (candidates.Count <= MaxDiagnosticsPerLayer)
            return candidates.Select(NormalizeDiagnostic).ToArray();

        var bounded = candidates
            .Take(MaxDiagnosticsPerLayer - 1)
            .Select(NormalizeDiagnostic)
            .ToList();
        bounded.Add(new PromptLayerDiagnostic(
            PromptLayerDiagnosticCode.DiagnosticsTruncated,
            $"omitted_count={candidates.Count - (MaxDiagnosticsPerLayer - 1)}"));
        return bounded;
    }

    private static PromptLayerDiagnostic NormalizeDiagnostic(PromptLayerDiagnostic diagnostic) =>
        diagnostic with { Detail = TruncateUtf8(diagnostic.Detail ?? string.Empty, MaxDiagnosticDetailUtf8Bytes) };

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;
            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }

    private static string Wrap(string delimiter, string content) =>
        $"<{delimiter}>\n{content.Trim()}\n</{delimiter}>";

    private static void Append(StringBuilder builder, string content)
    {
        if (builder.Length > 0)
            builder.Append("\n\n");
        builder.Append(content.Trim());
    }
}
