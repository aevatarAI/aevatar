using System.Collections.Immutable;

namespace Aevatar.Workflow.Abstractions.Workflows;

public static class WorkflowYamlRootSchema
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Version { get; } = "aevatar.workflow.v1";

    public static ImmutableArray<string> AcceptedRootFieldOrder { get; } =
    [
        "name",
        "description",
        "when_to_use",
        "configuration",
        "roles",
        "steps",
        "on_failure",
    ];

    public static ImmutableArray<string> AuthorableRootFieldOrder { get; } =
    [
        "name",
        "description",
        "configuration",
        "roles",
        "steps",
    ];

    public static ImmutableArray<string> UnsupportedDialectRootFieldOrder { get; } =
    [
        "version",
        "inputs",
        "outputs",
        "triggers",
        "on",
        "env",
        "jobs",
    ];

    public static ImmutableHashSet<string> AcceptedRootFields { get; } =
        ImmutableHashSet.CreateRange(Comparer, AcceptedRootFieldOrder);

    public static ImmutableHashSet<string> AuthorableRootFields { get; } =
        ImmutableHashSet.CreateRange(Comparer, AuthorableRootFieldOrder);

    public static ImmutableHashSet<string> UnsupportedDialectRootFields { get; } =
        ImmutableHashSet.CreateRange(Comparer, UnsupportedDialectRootFieldOrder);

    public static bool IsAcceptedRootField(string? field) =>
        !string.IsNullOrWhiteSpace(field) && AcceptedRootFields.Contains(field.Trim());

    public static string FormatAcceptedRootFields() => string.Join(", ", AcceptedRootFieldOrder);

    public static string FormatAuthorableRootFields() => string.Join(", ", AuthorableRootFieldOrder);

    public static string FormatUnsupportedDialectRootFields() => string.Join(", ", UnsupportedDialectRootFieldOrder);
}
