namespace Aevatar.Scripting.Abstractions.Behaviors;

public static class ScriptRuntimeSemanticsExtensions
{
    public static bool TryGetMessageSemantics(
        this ScriptRuntimeSemanticsSpec? spec,
        string typeUrl,
        ScriptMessageKind expectedKind,
        out ScriptMessageSemanticsSpec semantics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeUrl);

        if (spec != null)
        {
            foreach (var candidate in spec.Messages)
            {
                if (string.Equals(candidate.TypeUrl, typeUrl, StringComparison.Ordinal) &&
                    (expectedKind == ScriptMessageKind.Unspecified || candidate.Kind == expectedKind))
                {
                    semantics = candidate;
                    return true;
                }
            }

            if (expectedKind == ScriptMessageKind.Unspecified)
            {
                foreach (var candidate in spec.Messages)
                {
                    if (string.Equals(candidate.TypeUrl, typeUrl, StringComparison.Ordinal))
                    {
                        semantics = candidate;
                        return true;
                    }
                }
            }
        }

        semantics = new ScriptMessageSemanticsSpec();
        return false;
    }

    public static bool TryGetMessageSemantics(
        this ScriptRuntimeSemanticsSpec? spec,
        string typeUrl,
        out ScriptMessageSemanticsSpec semantics) =>
        TryGetMessageSemantics(spec, typeUrl, ScriptMessageKind.Unspecified, out semantics);

    public static ScriptMessageSemanticsSpec GetRequiredMessageSemantics(
        this ScriptRuntimeSemanticsSpec? spec,
        string typeUrl,
        ScriptMessageKind expectedKind)
    {
        if (spec.TryGetMessageSemantics(typeUrl, expectedKind, out var semantics))
            return semantics;

        throw new InvalidOperationException(
            $"Runtime semantics are missing for message type `{typeUrl}` with kind `{expectedKind}`.");
    }

    public static bool TryGetQuerySemantics(
        this ScriptRuntimeSemanticsSpec? spec,
        string queryTypeUrl,
        out ScriptQuerySemanticsSpec semantics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryTypeUrl);

        if (spec != null)
        {
            foreach (var candidate in spec.Queries)
            {
                if (string.Equals(candidate.QueryTypeUrl, queryTypeUrl, StringComparison.Ordinal))
                {
                    semantics = candidate;
                    return true;
                }
            }
        }

        semantics = new ScriptQuerySemanticsSpec();
        return false;
    }

    public static ScriptQuerySemanticsSpec GetRequiredQuerySemantics(
        this ScriptRuntimeSemanticsSpec? spec,
        string queryTypeUrl)
    {
        if (spec.TryGetQuerySemantics(queryTypeUrl, out var semantics))
            return semantics;

        throw new InvalidOperationException($"Runtime semantics are missing for query type `{queryTypeUrl}`.");
    }
}
