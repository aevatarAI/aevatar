using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sisyphus.Application.Models.Graph;
using Sisyphus.Application.Models.Paper;

namespace Sisyphus.Application.Services;

public class PaperGeneratorService(
    IOptions<PaperOptions> paperOptions,
    ILogger<PaperGeneratorService> logger)
{
    // amsthm theorem-like environments (proof uses built-in \begin{proof})
    private static readonly Dictionary<string, string> TheoremEnvNames = new()
    {
        ["theorem"] = "theorem",
        ["lemma"] = "lemma",
        ["definition"] = "definition",
        ["corollary"] = "corollary",
        ["conjecture"] = "conjecture",
        ["proposition"] = "proposition",
        ["remark"] = "remark",
        ["conclusion"] = "conclusion",
        ["example"] = "example",
        ["notation"] = "notation",
        ["axiom"] = "axiom",
        ["observation"] = "observation",
        ["note"] = "note",
    };

    /// <summary>
    /// Generates a PDF from a blue graph snapshot using LaTeX + tectonic.
    /// </summary>
    public virtual async Task<byte[]> GeneratePdfAsync(BlueGraphSnapshot snapshot, CancellationToken ct = default)
    {
        var sortedNodes = TopologicalSort(snapshot);
        var latex = GenerateLatex(sortedNodes, snapshot.Edges);

        logger.LogInformation("Generated LaTeX document: {Length} chars, {NodeCount} nodes",
            latex.Length, sortedNodes.Count);

        return await CompilePdfAsync(latex, ct);
    }

    /// <summary>
    /// Topological sort via Kahn's algorithm. Falls back to type-based ordering on cycles.
    /// </summary>
    internal static List<GraphNode> TopologicalSort(BlueGraphSnapshot snapshot)
    {
        var nodeIds = new HashSet<string>(snapshot.Nodes.Select(n => n.Id));
        var adjacency = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();

        foreach (var node in snapshot.Nodes)
        {
            adjacency[node.Id] = [];
            inDegree[node.Id] = 0;
        }

        foreach (var edge in snapshot.Edges)
        {
            if (!nodeIds.Contains(edge.Source) || !nodeIds.Contains(edge.Target))
                continue;

            // edge: source depends on target (source references/proves target)
            // so target should come before source
            adjacency[edge.Target].Add(edge.Source);
            inDegree[edge.Source] = inDegree.GetValueOrDefault(edge.Source) + 1;
        }

        var queue = new Queue<string>();
        foreach (var (id, deg) in inDegree)
        {
            if (deg == 0)
                queue.Enqueue(id);
        }

        var sorted = new List<string>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            foreach (var neighbor in adjacency.GetValueOrDefault(current, []))
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        var nodeMap = snapshot.Nodes.ToDictionary(n => n.Id);

        if (sorted.Count == snapshot.Nodes.Count)
            return sorted.Select(id => nodeMap[id]).ToList();

        // Cycle detected — fall back to type-based ordering
        var typeOrder = new Dictionary<string, int>
        {
            ["axiom"] = 0, ["notation"] = 1, ["definition"] = 2,
            ["lemma"] = 3, ["proposition"] = 4, ["theorem"] = 5,
            ["corollary"] = 6, ["conjecture"] = 7, ["proof"] = 8,
            ["example"] = 9, ["remark"] = 10, ["observation"] = 11,
            ["note"] = 12, ["conclusion"] = 13,
        };

        return snapshot.Nodes
            .OrderBy(n => typeOrder.GetValueOrDefault(n.Type, 99))
            .ToList();
    }

    internal static string GenerateLatex(List<GraphNode> sortedNodes, List<GraphEdge> edges)
    {
        var sb = new StringBuilder();

        // Preamble
        sb.AppendLine(@"\documentclass[11pt,a4paper]{article}");
        sb.AppendLine(@"\usepackage[utf8]{inputenc}");
        sb.AppendLine(@"\usepackage{amsmath,amssymb,amsthm}");
        sb.AppendLine(@"\usepackage{mathtools,mathrsfs,bm}");
        sb.AppendLine(@"\usepackage{hyperref}");
        sb.AppendLine(@"\usepackage{xcolor}");
        // Gracefully handle undefined commands from LLM-generated body content
        sb.AppendLine(@"\newcommand{\placeholder}[1]{\textbf{#1}}");
        // Common commands LLMs use that may not be defined
        sb.AppendLine(@"\providecommand{\N}{\mathbb{N}}");
        sb.AppendLine(@"\providecommand{\Z}{\mathbb{Z}}");
        sb.AppendLine(@"\providecommand{\R}{\mathbb{R}}");
        sb.AppendLine(@"\providecommand{\C}{\mathbb{C}}");
        sb.AppendLine(@"\providecommand{\Q}{\mathbb{Q}}");
        sb.AppendLine(@"\providecommand{\eps}{\varepsilon}");
        sb.AppendLine(@"\providecommand{\ind}{\mathbf{1}}");
        sb.AppendLine(@"\providecommand{\norm}[1]{\lVert #1 \rVert}");
        sb.AppendLine(@"\providecommand{\abs}[1]{\lvert #1 \rvert}");
        sb.AppendLine(@"\providecommand{\ceil}[1]{\lceil #1 \rceil}");
        sb.AppendLine(@"\providecommand{\floor}[1]{\lfloor #1 \rfloor}");
        sb.AppendLine(@"\providecommand{\inner}[2]{\langle #1, #2 \rangle}");
        sb.AppendLine(@"\providecommand{\Hom}{\operatorname{Hom}}");
        sb.AppendLine(@"\providecommand{\Aut}{\operatorname{Aut}}");
        sb.AppendLine(@"\providecommand{\End}{\operatorname{End}}");
        sb.AppendLine(@"\providecommand{\Spec}{\operatorname{Spec}}");
        sb.AppendLine(@"\providecommand{\Proj}{\operatorname{Proj}}");
        sb.AppendLine(@"\providecommand{\colim}{\operatorname{colim}}");
        sb.AppendLine(@"\providecommand{\rank}{\operatorname{rank}}");
        sb.AppendLine(@"\providecommand{\tr}{\operatorname{tr}}");
        sb.AppendLine(@"\providecommand{\diag}{\operatorname{diag}}");
        sb.AppendLine(@"\providecommand{\sgn}{\operatorname{sgn}}");
        sb.AppendLine(@"\providecommand{\supp}{\operatorname{supp}}");
        sb.AppendLine(@"\providecommand{\id}{\mathrm{id}}");
        sb.AppendLine();

        // Declare amsthm environments with shared counter and correct styles (proof is built-in)
        // Plain style (italic body): theorem, lemma, proposition, corollary, conjecture
        sb.AppendLine(@"\newtheorem{theorem}{Theorem}");
        sb.AppendLine(@"\newtheorem{lemma}[theorem]{Lemma}");
        sb.AppendLine(@"\newtheorem{proposition}[theorem]{Proposition}");
        sb.AppendLine(@"\newtheorem{corollary}[theorem]{Corollary}");
        sb.AppendLine(@"\newtheorem{conjecture}[theorem]{Conjecture}");

        // Definition style (upright body): definition, example, notation, axiom
        sb.AppendLine(@"\theoremstyle{definition}");
        sb.AppendLine(@"\newtheorem{definition}[theorem]{Definition}");
        sb.AppendLine(@"\newtheorem{example}[theorem]{Example}");
        sb.AppendLine(@"\newtheorem{notation}[theorem]{Notation}");
        sb.AppendLine(@"\newtheorem{axiom}[theorem]{Axiom}");

        // Remark style (upright body, smaller title): remark, observation, note, conclusion
        sb.AppendLine(@"\theoremstyle{remark}");
        sb.AppendLine(@"\newtheorem{remark}[theorem]{Remark}");
        sb.AppendLine(@"\newtheorem{observation}[theorem]{Observation}");
        sb.AppendLine(@"\newtheorem{note}[theorem]{Note}");
        sb.AppendLine(@"\newtheorem{conclusion}[theorem]{Conclusion}");

        // Pre-sanitize all bodies and collect unknown commands
        var sanitizedBodies = new Dictionary<string, string>();
        foreach (var node in sortedNodes)
        {
            sanitizedBodies[node.Id] = SanitizeBody(GetStringProperty(node.Properties, "body"));
        }

        // Scan all sanitized bodies for \commandname patterns and auto-define unknown ones
        var allBodyText = string.Join("\n", sanitizedBodies.Values);
        var unknownCommands = FindUnknownCommands(allBodyText);
        foreach (var cmd in unknownCommands)
        {
            sb.AppendLine($@"\providecommand{{\{cmd}}}{{}}");
        }

        sb.AppendLine();
        sb.AppendLine(@"\begin{document}");
        sb.AppendLine(@"\title{Knowledge Graph Paper}");
        sb.AppendLine(@"\maketitle");
        sb.AppendLine();

        // Build edge lookup for cross-references
        var nodeIdSet = new HashSet<string>(sortedNodes.Select(n => n.Id));
        var referencedBy = new Dictionary<string, List<string>>();
        foreach (var edge in edges)
        {
            if (!nodeIdSet.Contains(edge.Source) || !nodeIdSet.Contains(edge.Target))
                continue;
            if (!referencedBy.ContainsKey(edge.Source))
                referencedBy[edge.Source] = [];
            referencedBy[edge.Source].Add(edge.Target);
        }

        // Emit each node
        for (var i = 0; i < sortedNodes.Count; i++)
        {
            var node = sortedNodes[i];
            var label = $"node:{node.Id}";
            var nodeAbstract = GetStringProperty(node.Properties, "abstract");
            var body = sanitizedBodies[node.Id];

            if (node.Type == "proof")
            {
                // proof uses built-in LaTeX proof environment
                sb.AppendLine(@"\begin{proof}");
                sb.AppendLine($@"\label{{{label}}}");
                if (!string.IsNullOrWhiteSpace(nodeAbstract))
                    sb.AppendLine($@"\textit{{{EscapeLatex(nodeAbstract)}}}");
                sb.AppendLine();
                sb.AppendLine(body);
                EmitCrossRefs(sb, node.Id, referencedBy);
                sb.AppendLine(@"\end{proof}");
            }
            else if (TheoremEnvNames.TryGetValue(node.Type, out var envName))
            {
                sb.AppendLine($@"\begin{{{envName}}}");
                sb.AppendLine($@"\label{{{label}}}");
                if (!string.IsNullOrWhiteSpace(nodeAbstract))
                    sb.AppendLine($@"\textit{{{EscapeLatex(nodeAbstract)}}}");
                sb.AppendLine();
                sb.AppendLine(body);
                EmitCrossRefs(sb, node.Id, referencedBy);
                sb.AppendLine($@"\end{{{envName}}}");
            }
            else
            {
                // Unknown type — emit as paragraph
                sb.AppendLine($@"\paragraph{{{EscapeLatex(node.Type)}}}");
                sb.AppendLine($@"\label{{{label}}}");
                if (!string.IsNullOrWhiteSpace(nodeAbstract))
                    sb.AppendLine($@"\textit{{{EscapeLatex(nodeAbstract)}}}");
                sb.AppendLine();
                sb.AppendLine(body);
                EmitCrossRefs(sb, node.Id, referencedBy);
            }

            sb.AppendLine();
        }

        sb.AppendLine(@"\end{document}");
        return sb.ToString();
    }

    private static void EmitCrossRefs(
        StringBuilder sb, string nodeId, Dictionary<string, List<string>> referencedBy)
    {
        if (!referencedBy.TryGetValue(nodeId, out var targets) || targets.Count == 0)
            return;

        sb.Append(@"\noindent\textit{See also: ");
        sb.Append(string.Join(", ", targets.Select(t => $@"\ref{{node:{t}}}")));
        sb.AppendLine("}");
    }

    /// <summary>
    /// Standard LaTeX/AMS commands that should NOT be auto-defined via providecommand.
    /// </summary>
    private static readonly HashSet<string> KnownCommands = new(StringComparer.Ordinal)
    {
        // Document structure
        "documentclass", "usepackage", "begin", "end", "title", "author", "date", "maketitle",
        "section", "subsection", "subsubsection", "paragraph", "subparagraph",
        "chapter", "part", "appendix", "tableofcontents",
        // Text formatting
        "textbf", "textit", "texttt", "textrm", "textsf", "textsc", "emph", "underline",
        "tiny", "scriptsize", "footnotesize", "small", "normalsize", "large", "Large",
        "LARGE", "huge", "Huge", "bf", "it", "tt", "rm", "sf", "sc", "sl",
        // Math general
        "frac", "dfrac", "tfrac", "sqrt", "root", "overline", "underline", "widehat", "widetilde",
        "hat", "tilde", "bar", "vec", "dot", "ddot", "acute", "grave", "check", "breve",
        "mathbb", "mathbf", "mathcal", "mathfrak", "mathrm", "mathsf", "mathit", "mathtt",
        "boldsymbol", "bm", "operatorname", "DeclareMathOperator",
        "text", "mbox", "hbox", "vbox",
        // Math symbols & operators (amsmath/amssymb)
        "alpha", "beta", "gamma", "delta", "epsilon", "varepsilon", "zeta", "eta", "theta",
        "vartheta", "iota", "kappa", "lambda", "mu", "nu", "xi", "pi", "varpi",
        "rho", "varrho", "sigma", "varsigma", "tau", "upsilon", "phi", "varphi",
        "chi", "psi", "omega",
        "Gamma", "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Upsilon",
        "Phi", "Psi", "Omega",
        "le", "leq", "ge", "geq", "neq", "approx", "equiv", "sim", "simeq", "cong",
        "subset", "supset", "subseteq", "supseteq", "in", "notin", "ni",
        "cup", "cap", "setminus", "emptyset", "varnothing",
        "forall", "exists", "nexists", "neg", "lnot", "wedge", "vee", "land", "lor",
        "to", "rightarrow", "leftarrow", "leftrightarrow", "Rightarrow", "Leftarrow",
        "Leftrightarrow", "implies", "iff", "mapsto", "longmapsto",
        "uparrow", "downarrow", "Uparrow", "Downarrow", "updownarrow",
        "hookrightarrow", "hookleftarrow", "rightharpoonup", "rightharpoondown",
        "times", "div", "cdot", "cdots", "ldots", "vdots", "ddots", "dots",
        "pm", "mp", "circ", "bullet", "star", "ast", "oplus", "otimes", "odot",
        "sum", "prod", "coprod", "int", "oint", "iint", "iiint",
        "bigcup", "bigcap", "bigoplus", "bigotimes", "bigsqcup",
        "lim", "limsup", "liminf", "sup", "inf", "max", "min",
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan",
        "sinh", "cosh", "tanh", "coth",
        "log", "ln", "exp", "det", "dim", "ker", "gcd", "lcm", "deg", "hom",
        "arg", "mod", "pmod", "bmod",
        "infty", "partial", "nabla", "prime",
        "langle", "rangle", "lfloor", "rfloor", "lceil", "rceil",
        "lvert", "rvert", "lVert", "rVert", "vert", "Vert",
        "left", "right", "big", "Big", "bigg", "Bigg", "biggl", "biggr",
        // Spacing & layout
        "quad", "qquad", "hspace", "vspace", "hfill", "vfill",
        "noindent", "indent", "par", "newline", "linebreak", "pagebreak", "newpage",
        "centering", "raggedright", "raggedleft",
        // References & labels
        "label", "ref", "eqref", "cite", "footnote", "footnotemark", "footnotetext",
        // Environments & lists
        "item",
        // Tables & figures
        "hline", "cline", "multicolumn", "multirow",
        "includegraphics", "caption",
        // amsthm
        "qed", "qedhere", "proof",
        // Other common
        "input", "include", "newcommand", "renewcommand", "providecommand",
        "def", "let", "makeatletter", "makeatother",
        "setlength", "addtolength",
        "textasciitilde", "textasciicircum", "textbackslash",
        "LaTeX", "TeX",
        // Provided by our preamble
        "N", "Z", "R", "C", "Q", "eps", "ind", "norm", "abs", "ceil", "floor", "inner",
        "Hom", "Aut", "End", "Spec", "Proj", "colim", "rank", "tr", "diag", "sgn", "supp", "id",
        "placeholder",
        // xcolor
        "color", "textcolor", "colorbox",
        // hyperref
        "href", "url",
        // mathtools
        "coloneqq", "eqqcolon", "vcentcolon",
    };

    /// <summary>
    /// Finds all \commandname patterns in text that are not in the known commands set.
    /// </summary>
    internal static HashSet<string> FindUnknownCommands(string text)
    {
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"\\([a-zA-Z]+)"))
        {
            var name = match.Groups[1].Value;
            if (!KnownCommands.Contains(name))
                unknown.Add(name);
        }
        return unknown;
    }

    /// <summary>
    /// Validates LaTeX structural integrity via a state machine tracking math mode,
    /// brace depth, and environment nesting. Returns true if the body would cause
    /// cascading errors.
    /// </summary>
    internal static bool IsBodyBroken(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        // State machine walk
        var braceDepth = 0;
        var inMath = false;  // inside $...$
        var inDisplayMath = false; // inside $$...$$
        var negBraceHits = 0;
        var mathErrors = 0;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            // Skip escaped characters
            if (c == '\\' && i + 1 < body.Length)
            {
                var next = body[i + 1];
                if (next == '$' || next == '{' || next == '}' || next == '\\')
                {
                    i++;
                    continue;
                }
                // Skip \command sequences
                if (char.IsLetter(next))
                {
                    i++;
                    while (i + 1 < body.Length && char.IsLetter(body[i + 1])) i++;
                    continue;
                }
                i++;
                continue;
            }

            switch (c)
            {
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth--;
                    if (braceDepth < 0) negBraceHits++;
                    break;
                case '$':
                    // Check for $$
                    if (i + 1 < body.Length && body[i + 1] == '$')
                    {
                        if (inMath) mathErrors++; // $$ inside $
                        inDisplayMath = !inDisplayMath;
                        i++; // skip second $
                    }
                    else
                    {
                        if (inDisplayMath) mathErrors++; // $ inside $$
                        inMath = !inMath;
                    }
                    break;
                case '_':
                case '^':
                    // Subscript/superscript outside math mode = guaranteed error
                    if (!inMath && !inDisplayMath) mathErrors++;
                    break;
            }
        }

        // Any structural issue = broken
        if (negBraceHits > 0) return true;
        if (braceDepth != 0) return true; // shouldn't happen after our balancer, but check
        if (inMath || inDisplayMath) return true; // unclosed math mode
        if (mathErrors > 0) return true;

        // Check for unmatched environments
        var envStack = new Stack<string>();
        foreach (Match m in Regex.Matches(body, @"\\(begin|end)\{([^}]+)\}"))
        {
            var action = m.Groups[1].Value;
            var env = m.Groups[2].Value;
            if (action == "begin")
                envStack.Push(env);
            else if (envStack.Count > 0 && envStack.Peek() == env)
                envStack.Pop();
            else
                return true; // mismatched \end
        }
        if (envStack.Count > 0) return true;

        return false;
    }

    private static string GetStringProperty(Dictionary<string, JsonElement> properties, string key)
    {
        if (!properties.TryGetValue(key, out var element))
            return "";
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
    }

    private static readonly string[] DangerousEnvs =
    [
        "theorem", "lemma", "definition", "corollary", "conjecture",
        "proposition", "remark", "conclusion", "example", "notation",
        "axiom", "observation", "note", "proof", "document",
    ];

    /// <summary>
    /// Unicode characters that appear in LLM-generated text and must be replaced
    /// with LaTeX math-mode equivalents to avoid "Missing character" font errors.
    /// </summary>
    private static readonly Dictionary<string, string> UnicodeReplacements = new()
    {
        // Greek letters
        ["α"] = @"$\alpha$", ["β"] = @"$\beta$", ["γ"] = @"$\gamma$", ["δ"] = @"$\delta$",
        ["ε"] = @"$\varepsilon$", ["ζ"] = @"$\zeta$", ["η"] = @"$\eta$", ["θ"] = @"$\theta$",
        ["ι"] = @"$\iota$", ["κ"] = @"$\kappa$", ["λ"] = @"$\lambda$", ["μ"] = @"$\mu$",
        ["ν"] = @"$\nu$", ["ξ"] = @"$\xi$", ["π"] = @"$\pi$", ["ρ"] = @"$\rho$",
        ["σ"] = @"$\sigma$", ["τ"] = @"$\tau$", ["υ"] = @"$\upsilon$", ["φ"] = @"$\varphi$",
        ["χ"] = @"$\chi$", ["ψ"] = @"$\psi$", ["ω"] = @"$\omega$",
        ["Γ"] = @"$\Gamma$", ["Δ"] = @"$\Delta$", ["Θ"] = @"$\Theta$", ["Λ"] = @"$\Lambda$",
        ["Ξ"] = @"$\Xi$", ["Π"] = @"$\Pi$", ["Σ"] = @"$\Sigma$", ["Φ"] = @"$\Phi$",
        ["Ψ"] = @"$\Psi$", ["Ω"] = @"$\Omega$",
        // Math symbols
        ["≠"] = @"$\neq$", ["≤"] = @"$\leq$", ["≥"] = @"$\geq$", ["≈"] = @"$\approx$",
        ["∈"] = @"$\in$", ["∉"] = @"$\notin$", ["⊂"] = @"$\subset$", ["⊃"] = @"$\supset$",
        ["⊆"] = @"$\subseteq$", ["⊇"] = @"$\supseteq$", ["∪"] = @"$\cup$", ["∩"] = @"$\cap$",
        ["∅"] = @"$\emptyset$", ["∞"] = @"$\infty$", ["∀"] = @"$\forall$", ["∃"] = @"$\exists$",
        ["¬"] = @"$\neg$", ["∧"] = @"$\wedge$", ["∨"] = @"$\vee$",
        ["→"] = @"$\to$", ["←"] = @"$\leftarrow$", ["↔"] = @"$\leftrightarrow$",
        ["⇒"] = @"$\Rightarrow$", ["⇐"] = @"$\Leftarrow$", ["⇔"] = @"$\Leftrightarrow$",
        ["×"] = @"$\times$", ["÷"] = @"$\div$", ["±"] = @"$\pm$", ["∓"] = @"$\mp$",
        ["·"] = @"$\cdot$", ["∘"] = @"$\circ$", ["⊗"] = @"$\otimes$", ["⊕"] = @"$\oplus$",
        ["∑"] = @"$\sum$", ["∏"] = @"$\prod$", ["∫"] = @"$\int$",
        ["√"] = @"$\sqrt{}$", ["∂"] = @"$\partial$", ["∇"] = @"$\nabla$",
        ["ℝ"] = @"$\mathbb{R}$", ["ℤ"] = @"$\mathbb{Z}$", ["ℕ"] = @"$\mathbb{N}$",
        ["ℂ"] = @"$\mathbb{C}$", ["ℚ"] = @"$\mathbb{Q}$",
        ["⟨"] = @"$\langle$", ["⟩"] = @"$\rangle$",
        ["‖"] = @"$\|$", ["′"] = @"$'$",
    };

    internal static string SanitizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        // LLM-generated body often has double-escaped LaTeX commands (\\frac, \\( etc.)
        // from JSON serialization. Normalize \\<command> to \<command> but preserve
        // standalone \\ (LaTeX line break).
        body = body.Replace(@"\textbackslash{}", @"\");
        // \\<letter> → \<letter> (double-escaped commands like \\frac, \\le, \\sum)
        body = Regex.Replace(body, @"\\\\([a-zA-Z])", @"\$1");
        // \\( → \(  and \\[ → \[  and \\) → \)  and \\] → \]  (math delimiters)
        body = Regex.Replace(body, @"\\\\([\(\)\[\]])", @"\$1");

        // Strip dangerous commands that can cause fatal errors
        body = Regex.Replace(body, @"\\(input|include|usepackage|documentclass|bibliography|bibliographystyle)\b[^\\]*?(\n|$)", "\n");
        body = Regex.Replace(body, @"\\(input|include)\{[^}]*\}", "");

        // Strip \begin{env} and \end{env} for theorem-like environments
        // that the LLM may have included in body content, to prevent
        // mismatched environments that cause fatal LaTeX errors.
        foreach (var env in DangerousEnvs)
        {
            body = body.Replace($@"\begin{{{env}}}", "", StringComparison.Ordinal);
            body = body.Replace($@"\end{{{env}}}", "", StringComparison.Ordinal);
        }

        // Strip stray \begin{itemize/enumerate} without matching \end or vice versa
        foreach (var listEnv in new[] { "itemize", "enumerate" })
        {
            var opens = Regex.Matches(body, $@"\\begin\{{{listEnv}\}}").Count;
            var closes = Regex.Matches(body, $@"\\end\{{{listEnv}\}}").Count;
            if (opens != closes)
            {
                body = body.Replace($@"\begin{{{listEnv}}}", "", StringComparison.Ordinal);
                body = body.Replace($@"\end{{{listEnv}}}", "", StringComparison.Ordinal);
                body = body.Replace(@"\item", "• ", StringComparison.Ordinal);
            }
        }

        // Replace Unicode math/Greek characters with LaTeX equivalents
        foreach (var (unicode, latex) in UnicodeReplacements)
            body = body.Replace(unicode, latex);

        // Remove backtick references that cause "failed to open input file" errors
        body = body.Replace("`", "'");

        // Validate structural integrity BEFORE attempting fixes.
        // If broken, escape as plain text — don't try to patch it.
        if (IsBodyBroken(body))
            return EscapeLatex(body);

        return body;
    }

    private static string EscapeLatex(string text)
    {
        // Order matters: replace backslash first using a placeholder to avoid
        // subsequent replacements mangling the braces in \textbackslash{}.
        const string placeholder = "\x00BACKSLASH\x00";
        return text
            .Replace(@"\", placeholder)
            .Replace("{", @"\{")
            .Replace("}", @"\}")
            .Replace("&", @"\&")
            .Replace("%", @"\%")
            .Replace("$", @"\$")
            .Replace("#", @"\#")
            .Replace("_", @"\_")
            .Replace("~", @"\textasciitilde{}")
            .Replace("^", @"\textasciicircum{}")
            .Replace(placeholder, @"\textbackslash{}");
    }

    private async Task<byte[]> CompilePdfAsync(string latex, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sisyphus-paper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var texPath = Path.Combine(tempDir, "paper.tex");
            var pdfPath = Path.Combine(tempDir, "paper.pdf");

            await File.WriteAllTextAsync(texPath, latex, ct);

            var timeout = TimeSpan.FromSeconds(paperOptions.Value.CompileTimeoutSeconds);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "tectonic",
                Arguments = $"--untrusted -Z continue-on-errors {texPath}",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            logger.LogInformation("Compiling LaTeX with tectonic in {TempDir}", tempDir);
            process.Start();

            // Read stdout/stderr concurrently to avoid pipe buffer deadlock
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException(
                    $"Tectonic compilation timed out after {paperOptions.Value.CompileTimeoutSeconds}s");
            }

            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                logger.LogWarning("Tectonic exited with code {ExitCode}: {Stderr}",
                    process.ExitCode, stderr);

                // With -Z continue-on-errors, tectonic may still produce a PDF
                if (!File.Exists(pdfPath))
                {
                    logger.LogError("Tectonic did not produce a PDF file despite continue-on-errors");
                    throw new InvalidOperationException(
                        $"Tectonic compilation failed (exit code {process.ExitCode})");
                }

                logger.LogInformation("Tectonic produced a PDF despite errors — using it");
            }

            if (!File.Exists(pdfPath))
                throw new InvalidOperationException("Tectonic did not produce a PDF file");

            var pdfBytes = await File.ReadAllBytesAsync(pdfPath, ct);
            logger.LogInformation("PDF compiled successfully: {Size} bytes", pdfBytes.Length);
            return pdfBytes;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up temp directory {TempDir}", tempDir);
            }
        }
    }
}
