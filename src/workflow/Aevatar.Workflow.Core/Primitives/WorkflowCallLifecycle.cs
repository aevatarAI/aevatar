namespace Aevatar.Workflow.Core.Primitives;

internal static class WorkflowCallLifecycle
{
    public const string Singleton = "singleton";
    public const string Transient = "transient";
    public const string Scope = "scope";
    public static IReadOnlyList<string> AllowedValues { get; } = Array.AsReadOnly(new[] { Singleton, Transient, Scope });

    public static string AllowedValuesText => $"{Singleton}/{Transient}/{Scope}";

    public static bool IsSupported(string? lifecycle)
    {
        if (string.IsNullOrWhiteSpace(lifecycle))
            return true;

        return AllowedValues.Contains(lifecycle.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string? lifecycle)
    {
        if (string.IsNullOrWhiteSpace(lifecycle))
            return Singleton;

        if (string.Equals(lifecycle, Transient, StringComparison.OrdinalIgnoreCase))
            return Transient;
        if (string.Equals(lifecycle, Scope, StringComparison.OrdinalIgnoreCase))
            return Scope;

        return Singleton;
    }
}
