using System.Text.RegularExpressions;

namespace Aevatar.Workflow.Core.Expressions;

internal sealed partial class WorkflowExpressionParser
{
    [GeneratedRegex(@"\$\{([^}]+)\}", RegexOptions.Compiled)]
    private static partial Regex ExpressionPattern();

    public WorkflowExpressionTemplate ParseTemplate(string? template)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${", StringComparison.Ordinal))
            return new WorkflowExpressionTemplate([new WorkflowTextSegment(template ?? string.Empty)]);

        var segments = new List<WorkflowExpressionSegment>();
        var start = 0;
        foreach (Match match in ExpressionPattern().Matches(template))
        {
            if (match.Index > start)
                segments.Add(new WorkflowTextSegment(template[start..match.Index]));

            segments.Add(new WorkflowInterpolatedExpressionSegment(ParseExpression(match.Groups[1].Value)));
            start = match.Index + match.Length;
        }

        if (start < template.Length)
            segments.Add(new WorkflowTextSegment(template[start..]));

        return new WorkflowExpressionTemplate(segments);
    }

    public WorkflowExpressionNode ParseExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return new WorkflowLiteralExpression(string.Empty);

        var trimmed = expression.Trim();
        if ((trimmed.StartsWith('\'') && trimmed.EndsWith('\'')) ||
            (trimmed.StartsWith('"') && trimmed.EndsWith('"')))
        {
            return new WorkflowLiteralExpression(trimmed[1..^1]);
        }

        var functionMatch = Regex.Match(trimmed, @"^(\w+)\((.*)\)$", RegexOptions.Singleline);
        if (functionMatch.Success)
        {
            var functionName = functionMatch.Groups[1].Value;
            var rawArguments = functionMatch.Groups[2].Value;
            return new WorkflowFunctionCallExpression(
                functionName,
                SplitArgs(rawArguments).Select(ParseExpression).ToList());
        }

        return new WorkflowVariableExpression(trimmed);
    }

    private static List<string> SplitArgs(string raw)
    {
        var args = new List<string>();
        var depth = 0;
        var inQuote = false;
        var quoteChar = ' ';
        var start = 0;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inQuote)
            {
                if (c == quoteChar)
                    inQuote = false;
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    inQuote = true;
                    quoteChar = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    args.Add(raw[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        if (start < raw.Length)
            args.Add(raw[start..].Trim());

        return args;
    }
}
