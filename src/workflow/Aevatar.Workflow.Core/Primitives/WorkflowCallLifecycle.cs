namespace Aevatar.Workflow.Core.Primitives;

internal static class WorkflowCallLifecycle
{
    public const string Singleton = "singleton";
    public const string Transient = "transient";
    public const string Scope = "scope";

    public static string Normalize(string? lifecycle)
    {
        if (string.Equals(lifecycle, Transient, StringComparison.OrdinalIgnoreCase))
            return Transient;
        if (string.Equals(lifecycle, Scope, StringComparison.OrdinalIgnoreCase))
            return Scope;

        return Singleton;
    }
}
