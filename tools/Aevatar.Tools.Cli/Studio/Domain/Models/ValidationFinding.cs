namespace Aevatar.Tools.Cli.Studio.Domain.Models;

public sealed record ValidationFinding(
    ValidationLevel Level,
    string Path,
    string Message,
    string? Hint = null,
    string? Code = null)
{
    public static ValidationFinding Error(string path, string message, string? hint = null, string? code = null) =>
        new(ValidationLevel.Error, path, message, hint, code);

    public static ValidationFinding Warning(string path, string message, string? hint = null, string? code = null) =>
        new(ValidationLevel.Warning, path, message, hint, code);
}
