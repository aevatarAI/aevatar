using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRunStatePatchContributorSupport
{
    public static bool AssignMapSliceIfChanged<TValue, TFacts>(
        MapField<string, TValue> current,
        MapField<string, TValue> next,
        Action<TFacts> assign,
        Func<MapField<string, TValue>, TFacts> createFacts)
        where TValue : class
        where TFacts : class
    {
        if (MapEquals(current, next))
            return false;

        assign(createFacts(next));
        return true;
    }

    public static bool AssignMapSliceIfChanged<TFacts>(
        MapField<string, string> current,
        MapField<string, string> next,
        Action<TFacts> assign,
        Func<MapField<string, string>, TFacts> createFacts)
        where TFacts : class
    {
        if (MapEquals(current, next))
            return false;

        assign(createFacts(next));
        return true;
    }

    public static bool AssignMapSliceIfChanged<TFacts>(
        MapField<string, int> current,
        MapField<string, int> next,
        Action<TFacts> assign,
        Func<MapField<string, int>, TFacts> createFacts)
        where TFacts : class
    {
        if (MapEquals(current, next))
            return false;

        assign(createFacts(next));
        return true;
    }

    public static bool AssignRepeatedSliceIfChanged(
        RepeatedField<string> current,
        RepeatedField<string> next,
        Action<WorkflowRunChildActorIdsFacts> assign)
    {
        if (RepeatedEquals(current, next))
            return false;

        var facts = new WorkflowRunChildActorIdsFacts();
        facts.ActorIds.Add(next);
        assign(facts);
        return true;
    }

    public static void ReplaceMapIfPresent<TValue>(MapField<string, TValue> target, MapField<string, TValue>? source)
    {
        if (source == null)
            return;

        ReplaceMap(target, source);
    }

    public static void ReplaceMap<TValue>(MapField<string, TValue> target, MapField<string, TValue> source)
    {
        target.Clear();
        CopyMap(source, target);
    }

    public static void ReplaceRepeatedIfPresent(RepeatedField<string> target, RepeatedField<string>? source)
    {
        if (source == null)
            return;

        target.Clear();
        target.Add(source);
    }

    public static void CopyMap<TValue>(MapField<string, TValue> source, MapField<string, TValue> target)
    {
        foreach (var (key, value) in source)
            target[key] = value;
    }

    public static bool MapEquals<TValue>(MapField<string, TValue> left, MapField<string, TValue> right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.Count != right.Count)
            return false;

        var comparer = EqualityComparer<TValue>.Default;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !comparer.Equals(value, other))
                return false;
        }

        return true;
    }

    public static bool RepeatedEquals(RepeatedField<string> left, RepeatedField<string> right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
