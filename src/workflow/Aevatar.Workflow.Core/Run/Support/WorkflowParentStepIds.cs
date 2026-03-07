namespace Aevatar.Workflow.Core;

internal static class WorkflowParentStepIds
{
    public static bool TryGetParallelParent(string stepId, out string parent)
    {
        var index = stepId.LastIndexOf("_sub_", StringComparison.Ordinal);
        if (index <= 0)
        {
            parent = string.Empty;
            return false;
        }

        parent = stepId[..index];
        return true;
    }

    public static string? TryGetForEachParent(string stepId)
    {
        const string marker = "_item_";
        var index = stepId.LastIndexOf(marker, StringComparison.Ordinal);
        if (index <= 0)
            return null;

        var suffix = stepId[(index + marker.Length)..];
        return suffix.All(char.IsDigit) ? stepId[..index] : null;
    }

    public static string? TryGetMapReduceParent(string stepId)
    {
        var index = stepId.LastIndexOf("_map_", StringComparison.Ordinal);
        return index > 0 ? stepId[..index] : null;
    }

    public static string? TryGetRaceParent(string stepId)
    {
        var index = stepId.LastIndexOf("_race_", StringComparison.Ordinal);
        return index > 0 ? stepId[..index] : null;
    }

    public static string? TryGetWhileParent(string stepId)
    {
        var index = stepId.LastIndexOf("_iter_", StringComparison.Ordinal);
        if (index <= 0)
            return null;

        var suffix = stepId[(index + "_iter_".Length)..];
        return suffix.All(char.IsDigit) ? stepId[..index] : null;
    }
}
