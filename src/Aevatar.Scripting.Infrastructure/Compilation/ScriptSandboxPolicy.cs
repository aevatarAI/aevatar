using System.Text.RegularExpressions;

namespace Aevatar.Scripting.Infrastructure.Compilation;

public sealed class ScriptSandboxPolicy
{
    private static readonly (Regex Pattern, string Rule)[] ForbiddenRules =
    {
        (new Regex(@"\bTask\s*\.\s*Run\s*\(", RegexOptions.Compiled), "Task.Run"),
        (new Regex(@"\bnew\s+Timer\s*\(", RegexOptions.Compiled), "new Timer"),
        (new Regex(@"\bnew\s+Thread\s*\(", RegexOptions.Compiled), "new Thread"),
        (new Regex(@"\block\s*\(", RegexOptions.Compiled), "lock"),
        (new Regex(@"\bFile\s*\.", RegexOptions.Compiled), "File.*"),
        (new Regex(@"\bDirectory\s*\.", RegexOptions.Compiled), "Directory.*"),
        (new Regex(@"\bFileStream\s*\(", RegexOptions.Compiled), "FileStream"),
        (new Regex(@"\bSystem\s*\.\s*IO\s*\.", RegexOptions.Compiled), "System.IO"),
        (new Regex(@"\bAssembly\s*\.\s*Load", RegexOptions.Compiled), "Assembly.Load*"),
        (new Regex(@"\btypeof\s*\([^)]*\)\s*\.\s*Assembly", RegexOptions.Compiled), "typeof(...).Assembly"),
        (new Regex(@"\bSystem\s*\.\s*Reflection\s*\.", RegexOptions.Compiled), "System.Reflection"),
        (new Regex(@"\bnew\s+HttpClient\s*\(", RegexOptions.Compiled), "new HttpClient"),
        (new Regex(@"\bWebRequest\s*\.", RegexOptions.Compiled), "WebRequest.*"),
        (new Regex(@"\bSocket\s*\(", RegexOptions.Compiled), "Socket"),
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
