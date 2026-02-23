namespace Aevatar.Demos.Maker;

public sealed class MakerCliOptions
{
    public const string LlmMode = "llm";
    public const string DeterministicMode = "deterministic";

    public static string DefaultInputText => """
        Please analyze the following paper excerpt with MAKER rigor and complete all tasks:
        1) Provide a one-sentence core claim.
        2) Explain the mechanism that enables scaling.
        3) List key assumptions and likely failure modes.
        4) Propose two concrete follow-up experiments.

        Paper excerpt:
        Large language models (LLMs) have achieved remarkable breakthroughs in reasoning,
        insights, and tool use, but chaining these abilities into extended processes at the
        scale of those routinely executed by humans, organizations, and societies has remained
        out of reach. The models have a persistent error rate that prevents scale-up.
        This paper describes MAKER, the first system that successfully solves a task with over
        one million LLM steps with zero errors. The approach relies on an extreme decomposition
        of a task into subtasks, each of which can be tackled by focused microagents. The high
        level of modularity resulting from the decomposition allows error correction to be
        applied at each step through an efficient multi-agent voting scheme. This combination
        of extreme decomposition and error correction makes scaling possible.
        """;

    public string Mode { get; }
    public string InputText { get; }
    public bool ShowHelp { get; }

    private MakerCliOptions(string mode, string inputText, bool showHelp)
    {
        Mode = mode;
        InputText = inputText;
        ShowHelp = showHelp;
    }

    public bool IsDeterministicMode =>
        string.Equals(Mode, DeterministicMode, StringComparison.OrdinalIgnoreCase);

    public static MakerCliOptions Parse(string[] args)
    {
        var mode = LlmMode;
        var showHelp = false;
        var inputParts = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("Missing value for --mode.");

                mode = args[++i].Trim();
                continue;
            }

            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase))
            {
                showHelp = true;
                continue;
            }

            inputParts.Add(arg);
        }

        if (!string.Equals(mode, LlmMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, DeterministicMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported mode '{mode}'. Expected '{LlmMode}' or '{DeterministicMode}'.");
        }

        return new MakerCliOptions(mode, string.Join(" ", inputParts), showHelp);
    }

    public static string BuildHelpText() => """
        Usage:
          dotnet run -- [--mode llm|deterministic] [input text]

        Modes:
          llm            Use configured LLM provider (default).
          deterministic  Use built-in deterministic provider to force full MAKER flow.

        Examples:
          dotnet run -- --mode deterministic
          dotnet run -- --mode llm -- "Analyze this paper section..."
        """;
}
