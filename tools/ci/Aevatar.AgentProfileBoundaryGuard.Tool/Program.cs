namespace Aevatar.AgentProfileBoundaryGuard.Tool;

internal static class Program
{
    private static int Main(string[] args) =>
        AgentProfileBoundaryGuardCli.Run(args, Console.Out, Console.Error);
}

internal static class AgentProfileBoundaryGuardCli
{
    private const string Usage =
        "Usage: dotnet Aevatar.AgentProfileBoundaryGuard.Tool.dll check --scan-root <root> [--scan-root <root> ...]";

    internal static int Run(
        IReadOnlyList<string> args,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        if (!TryParseRoots(args, out var roots))
        {
            standardError.WriteLine(Usage);
            return 2;
        }

        var checker = new AgentProfileAuthoritySyntaxChecker();
        var exitCode = 0;
        foreach (var root in roots)
        {
            try
            {
                var result = checker.Check(root);
                if (result.Violations.Count == 0)
                {
                    standardOutput.WriteLine($"PASS|{Safe(root)}");
                    continue;
                }

                exitCode = Math.Max(exitCode, 1);
                foreach (var violation in result.Violations)
                {
                    standardOutput.WriteLine(
                        $"VIOLATION|{Safe(root)}|{violation.Location}|{violation.Message}");
                }
            }
            catch (AgentProfileAuthorityInputException)
            {
                exitCode = 2;
                standardError.WriteLine($"ERROR|{Safe(root)}|Scan root or governed input is missing or unreadable.");
            }
            catch (Exception)
            {
                exitCode = 2;
                standardError.WriteLine($"ERROR|{Safe(root)}|Structured authority check failed.");
            }
        }

        return exitCode;
    }

    private static bool TryParseRoots(IReadOnlyList<string> args, out IReadOnlyList<string> roots)
    {
        var parsed = new List<string>();
        if (args.Count < 3 || !string.Equals(args[0], "check", StringComparison.Ordinal))
        {
            roots = parsed;
            return false;
        }

        for (var index = 1; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count ||
                !string.Equals(args[index], "--scan-root", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(args[index + 1]))
            {
                roots = parsed;
                return false;
            }
            parsed.Add(args[index + 1]);
        }

        roots = parsed;
        return parsed.Count > 0;
    }

    private static string Safe(string value) =>
        value.Replace('|', '_').Replace('\r', '_').Replace('\n', '_');
}
