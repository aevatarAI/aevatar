namespace Aevatar.Scripting.Abstractions.Definitions;

public sealed record ScriptEvolutionValidationReport(
    bool IsSuccess,
    IReadOnlyList<string> Diagnostics)
{
    public static readonly ScriptEvolutionValidationReport Empty = new(
        false,
        ["Validation was not executed."]);
}
