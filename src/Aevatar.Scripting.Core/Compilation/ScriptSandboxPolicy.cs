using System.Text.RegularExpressions;

namespace Aevatar.Scripting.Core.Compilation;

public sealed class ScriptSandboxPolicy
{
    private static readonly (Regex Pattern, string Rule)[] ForbiddenRules =
    {
        (new Regex(@"\bTask\s*\.\s*Run\s*\(", RegexOptions.Compiled), "Task.Run"),
        (new Regex(@"\bnew\s+Timer\s*\(", RegexOptions.Compiled), "new Timer"),
        (new Regex(@"\bnew\s+Thread\s*\(", RegexOptions.Compiled), "new Thread"),
        (new Regex(@"\block\s*\(", RegexOptions.Compiled), "lock"),
    };

    public ScriptSandboxValidationResult Validate(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new ScriptSandboxValidationResult(true, Array.Empty<string>());
        }

        var violations = new List<string>();
        foreach (var (pattern, rule) in ForbiddenRules)
        {
            if (pattern.IsMatch(source))
            {
                violations.Add($"Forbidden API usage detected: {rule}");
            }
        }

        return new ScriptSandboxValidationResult(violations.Count == 0, violations);
    }
}
