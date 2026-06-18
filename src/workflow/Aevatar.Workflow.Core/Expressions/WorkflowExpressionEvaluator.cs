// -------------------------------------------------------------
// WorkflowExpressionEvaluator - lightweight expression evaluator
// Supports variable interpolation, conditionals, and string functions
// for dynamic YAML workflow step parameters.
//
// Syntax:
//   ${variables.name}          - variable reference
//   ${if(cond, true, false)}   - conditional
//   ${concat(a, b, ...)}       - concatenation
//   ${isBlank(val)}            - blank value check
//   ${length(val)}             - length
//   ${not(val)}                - negation
//   ${and(a, b)}               - logical and
//   ${or(a, b)}                - logical or
// -------------------------------------------------------------

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.Workflow.Core.Expressions;

/// <summary>
/// Lightweight expression evaluator for workflow YAML parameters.
/// Evaluates ${...} expressions within strings using a variable context.
/// </summary>
public sealed partial class WorkflowExpressionEvaluator
{
    [GeneratedRegex(@"(?<!\$)\$\{([^}]+)\}", RegexOptions.Compiled)]
    private static partial Regex ExpressionPattern();

    /// <summary>
    /// Evaluates all ${...} expressions in a string template.
    /// Non-expression text is returned as-is.
    /// </summary>
    /// <param name="template">String potentially containing ${...} expressions.</param>
    /// <param name="variables">Variable context for resolution.</param>
    /// <returns>Evaluated string with expressions replaced by their values.</returns>
    public string Evaluate(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${"))
            return template;

        var result = new StringBuilder(template.Length);
        var cursor = 0;
        foreach (Match match in ExpressionPattern().Matches(template))
        {
            AppendLiteralSegment(result, template.AsSpan(cursor, match.Index - cursor));
            var expr = match.Groups[1].Value.Trim();
            result.Append(EvaluateExpression(expr, variables));
            cursor = match.Index + match.Length;
        }

        AppendLiteralSegment(result, template.AsSpan(cursor));
        return result.ToString();
    }

    private static void AppendLiteralSegment(StringBuilder result, ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length;)
        {
            if (i + 2 < value.Length &&
                value[i] == '$' &&
                value[i + 1] == '$' &&
                value[i + 2] == '{')
            {
                result.Append("${");
                i += 3;
                continue;
            }

            result.Append(value[i]);
            i++;
        }
    }

    /// <summary>
    /// Evaluates a single expression (without ${...} wrapper).
    /// </summary>
    public string EvaluateExpression(string expression, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "";

        // Function call: name(args...)
        var funcMatch = Regex.Match(expression, @"^(\w+)\((.+)\)$", RegexOptions.Singleline);
        if (funcMatch.Success)
        {
            var funcName = funcMatch.Groups[1].Value.ToLowerInvariant();
            var argsRaw = funcMatch.Groups[2].Value;
            var args = SplitArgs(argsRaw);

            return funcName switch
            {
                "if" => EvalIf(args, variables),
                "concat" => EvalConcat(args, variables),
                "isblank" => EvalIsBlank(args, variables),
                "length" => EvalLength(args, variables),
                "not" => EvalNot(args, variables),
                "and" => EvalAnd(args, variables),
                "or" => EvalOr(args, variables),
                "upper" => EvalUpper(args, variables),
                "lower" => EvalLower(args, variables),
                "trim" => EvalTrim(args, variables),
                "json" => EvalJson(args, variables),
                "add" => EvalAdd(args, variables),
                "sub" => EvalSub(args, variables),
                "mul" => EvalMul(args, variables),
                "div" => EvalDiv(args, variables),
                "eq" => EvalEq(args, variables),
                "lt" => EvalLt(args, variables),
                "lte" => EvalLte(args, variables),
                "gt" => EvalGt(args, variables),
                "gte" => EvalGte(args, variables),
                _ => $"[unknown function: {funcName}]",
            };
        }

        // Variable reference: variables.name or just name
        return ResolveVariable(expression, variables);
    }

    // Functions

    private string EvalIf(List<string> args, IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count < 3) return "[if: requires 3 args]";
        var condition = EvaluateExpression(args[0], variables);
        var isTruthy = IsTruthy(condition);
        return EvaluateExpression(isTruthy ? args[1] : args[2], variables);
    }

    private string EvalConcat(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        string.Concat(args.Select(a => EvaluateExpression(a, variables)));

    private string EvalIsBlank(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        args.Count > 0 && string.IsNullOrWhiteSpace(EvaluateExpression(args[0], variables)) ? "true" : "false";

    private string EvalLength(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        args.Count > 0 ? EvaluateExpression(args[0], variables).Length.ToString() : "0";

    private string EvalNot(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        args.Count > 0 && !IsTruthy(EvaluateExpression(args[0], variables)) ? "true" : "false";

    private string EvalAnd(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        args.Count >= 2 &&
        IsTruthy(EvaluateExpression(args[0], variables)) &&
        IsTruthy(EvaluateExpression(args[1], variables)) ? "true" : "false";

    private string EvalOr(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        args.Count >= 2 && (
            IsTruthy(EvaluateExpression(args[0], variables)) ||
            IsTruthy(EvaluateExpression(args[1], variables))) ? "true" : "false";

    private string EvalUpper(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        args.Count > 0 ? EvaluateExpression(args[0], variables).ToUpperInvariant() : "";

    private string EvalLower(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        args.Count > 0 ? EvaluateExpression(args[0], variables).ToLowerInvariant() : "";

    private string EvalTrim(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        args.Count > 0 ? EvaluateExpression(args[0], variables).Trim() : "";

    private string EvalJson(List<string> args, IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count == 0)
            return "";

        var value = EvaluateExpression(args[0], variables);
        return JsonEncodedText.Encode(value ?? string.Empty, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
    }

    private string EvalAdd(List<string> args, IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count == 0) return "0";
        double sum = 0;
        foreach (var arg in args)
        {
            if (!TryParseNumber(EvaluateExpression(arg, variables), out var number))
                return "[add: non-numeric argument]";
            sum += number;
        }
        return FormatNumber(sum);
    }

    private string EvalSub(List<string> args, IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count == 0) return "0";
        if (!TryParseNumber(EvaluateExpression(args[0], variables), out var result))
            return "[sub: non-numeric argument]";
        if (args.Count == 1)
            return FormatNumber(-result);

        for (var i = 1; i < args.Count; i++)
        {
            if (!TryParseNumber(EvaluateExpression(args[i], variables), out var number))
                return "[sub: non-numeric argument]";
            result -= number;
        }
        return FormatNumber(result);
    }

    private string EvalMul(List<string> args, IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count == 0) return "0";
        double product = 1;
        foreach (var arg in args)
        {
            if (!TryParseNumber(EvaluateExpression(arg, variables), out var number))
                return "[mul: non-numeric argument]";
            product *= number;
        }
        return FormatNumber(product);
    }

    private string EvalDiv(List<string> args, IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count < 2) return "[div: requires 2 args]";
        if (!TryParseNumber(EvaluateExpression(args[0], variables), out var result))
            return "[div: non-numeric argument]";

        for (var i = 1; i < args.Count; i++)
        {
            if (!TryParseNumber(EvaluateExpression(args[i], variables), out var number))
                return "[div: non-numeric argument]";
            if (Math.Abs(number) < double.Epsilon)
                return "[div: division by zero]";
            result /= number;
        }
        return FormatNumber(result);
    }

    private string EvalEq(List<string> args, IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count < 2) return "false";
        var left = EvaluateExpression(args[0], variables);
        var right = EvaluateExpression(args[1], variables);
        if (TryParseNumber(left, out var leftNum) && TryParseNumber(right, out var rightNum))
            return Math.Abs(leftNum - rightNum) < 1e-9 ? "true" : "false";
        return string.Equals(left, right, StringComparison.Ordinal) ? "true" : "false";
    }

    private string EvalLt(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        EvalNumericComparison(args, variables, static (l, r) => l < r, "lt");

    private string EvalLte(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        EvalNumericComparison(args, variables, static (l, r) => l <= r, "lte");

    private string EvalGt(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        EvalNumericComparison(args, variables, static (l, r) => l > r, "gt");

    private string EvalGte(List<string> args, IReadOnlyDictionary<string, string> variables) =>
        EvalNumericComparison(args, variables, static (l, r) => l >= r, "gte");

    // Helpers

    private static string ResolveVariable(string name, IReadOnlyDictionary<string, string> variables)
    {
        // Strip quotes for string literals
        if ((name.StartsWith('\'') && name.EndsWith('\'')) ||
            (name.StartsWith('"') && name.EndsWith('"')))
            return name[1..^1];

        // Dotted path: variables.name resolves the "name" key.
        var key = name.StartsWith("variables.", StringComparison.OrdinalIgnoreCase)
            ? name["variables.".Length..]
            : name;

        if (variables.TryGetValue(key, out var resolved))
            return resolved;
        if (TryParseNumber(key, out _))
            return key;
        if (bool.TryParse(key, out _))
            return key.ToLowerInvariant();
        return "";
    }

    private static bool IsTruthy(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (bool.TryParse(value, out var boolValue))
            return boolValue;
        if (TryParseNumber(value, out var number))
            return Math.Abs(number) >= 1e-9;
        return true;
    }

    private static bool TryParseNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    private static string FormatNumber(double number) =>
        number.ToString("G17", CultureInfo.InvariantCulture);

    private string EvalNumericComparison(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> variables,
        Func<double, double, bool> predicate,
        string functionName)
    {
        if (args.Count < 2) return $"[{functionName}: requires 2 args]";
        if (!TryParseNumber(EvaluateExpression(args[0], variables), out var left) ||
            !TryParseNumber(EvaluateExpression(args[1], variables), out var right))
            return $"[{functionName}: non-numeric argument]";
        return predicate(left, right) ? "true" : "false";
    }

    /// <summary>
    /// Split function arguments by comma, respecting nested parentheses and quotes.
    /// </summary>
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
                if (c == quoteChar) inQuote = false;
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
