using System.Globalization;

namespace Aevatar.Workflow.Core.Expressions;

internal sealed class WorkflowExpressionRuntimeEvaluator
{
    public string EvaluateTemplate(
        WorkflowExpressionTemplate template,
        IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        return string.Concat(template.Segments.Select(segment => EvaluateSegment(segment, variables)));
    }

    public string EvaluateExpression(
        WorkflowExpressionNode expression,
        IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(variables);

        return expression switch
        {
            WorkflowLiteralExpression literal => literal.Value,
            WorkflowVariableExpression variable => ResolveVariable(variable.Name, variables),
            WorkflowFunctionCallExpression function => EvaluateFunction(function, variables),
            _ => string.Empty,
        };
    }

    private string EvaluateSegment(
        WorkflowExpressionSegment segment,
        IReadOnlyDictionary<string, string> variables) =>
        segment switch
        {
            WorkflowTextSegment text => text.Text,
            WorkflowInterpolatedExpressionSegment expr => EvaluateExpression(expr.Expression, variables),
            _ => string.Empty,
        };

    private string EvaluateFunction(
        WorkflowFunctionCallExpression function,
        IReadOnlyDictionary<string, string> variables)
    {
        var name = function.Name.ToLowerInvariant();
        var args = function.Arguments;

        return name switch
        {
            "if" => EvalIf(args, variables),
            "concat" => string.Concat(args.Select(arg => EvaluateExpression(arg, variables))),
            "isblank" => args.Count > 0 && string.IsNullOrWhiteSpace(EvaluateExpression(args[0], variables)) ? "true" : "false",
            "length" => args.Count > 0 ? EvaluateExpression(args[0], variables).Length.ToString(CultureInfo.InvariantCulture) : "0",
            "not" => args.Count > 0 && !IsTruthy(EvaluateExpression(args[0], variables)) ? "true" : "false",
            "and" => args.Count >= 2 && IsTruthy(EvaluateExpression(args[0], variables)) && IsTruthy(EvaluateExpression(args[1], variables)) ? "true" : "false",
            "or" => args.Count >= 2 && (IsTruthy(EvaluateExpression(args[0], variables)) || IsTruthy(EvaluateExpression(args[1], variables))) ? "true" : "false",
            "upper" => args.Count > 0 ? EvaluateExpression(args[0], variables).ToUpperInvariant() : string.Empty,
            "lower" => args.Count > 0 ? EvaluateExpression(args[0], variables).ToLowerInvariant() : string.Empty,
            "trim" => args.Count > 0 ? EvaluateExpression(args[0], variables).Trim() : string.Empty,
            "add" => EvalAdd(args, variables),
            "sub" => EvalSub(args, variables),
            "mul" => EvalMul(args, variables),
            "div" => EvalDiv(args, variables),
            "eq" => EvalEq(args, variables),
            "lt" => EvalNumericComparison(args, variables, static (left, right) => left < right, "lt"),
            "lte" => EvalNumericComparison(args, variables, static (left, right) => left <= right, "lte"),
            "gt" => EvalNumericComparison(args, variables, static (left, right) => left > right, "gt"),
            "gte" => EvalNumericComparison(args, variables, static (left, right) => left >= right, "gte"),
            _ => $"[unknown function: {name}]",
        };
    }

    private string EvalIf(
        IReadOnlyList<WorkflowExpressionNode> args,
        IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count < 3)
            return "[if: requires 3 args]";

        var condition = EvaluateExpression(args[0], variables);
        return EvaluateExpression(IsTruthy(condition) ? args[1] : args[2], variables);
    }

    private string EvalAdd(
        IReadOnlyList<WorkflowExpressionNode> args,
        IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count == 0)
            return "0";

        double sum = 0;
        foreach (var arg in args)
        {
            if (!TryParseNumber(EvaluateExpression(arg, variables), out var number))
                return "[add: non-numeric argument]";
            sum += number;
        }

        return FormatNumber(sum);
    }

    private string EvalSub(
        IReadOnlyList<WorkflowExpressionNode> args,
        IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count == 0)
            return "0";

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

    private string EvalMul(
        IReadOnlyList<WorkflowExpressionNode> args,
        IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count == 0)
            return "0";

        double product = 1;
        foreach (var arg in args)
        {
            if (!TryParseNumber(EvaluateExpression(arg, variables), out var number))
                return "[mul: non-numeric argument]";
            product *= number;
        }

        return FormatNumber(product);
    }

    private string EvalDiv(
        IReadOnlyList<WorkflowExpressionNode> args,
        IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count < 2)
            return "[div: requires 2 args]";

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

    private string EvalEq(
        IReadOnlyList<WorkflowExpressionNode> args,
        IReadOnlyDictionary<string, string> variables)
    {
        if (args.Count < 2)
            return "false";

        var left = EvaluateExpression(args[0], variables);
        var right = EvaluateExpression(args[1], variables);
        if (TryParseNumber(left, out var leftNumber) && TryParseNumber(right, out var rightNumber))
            return Math.Abs(leftNumber - rightNumber) < 1e-9 ? "true" : "false";

        return string.Equals(left, right, StringComparison.Ordinal) ? "true" : "false";
    }

    private string EvalNumericComparison(
        IReadOnlyList<WorkflowExpressionNode> args,
        IReadOnlyDictionary<string, string> variables,
        Func<double, double, bool> predicate,
        string functionName)
    {
        if (args.Count < 2)
            return $"[{functionName}: requires 2 args]";

        if (!TryParseNumber(EvaluateExpression(args[0], variables), out var left) ||
            !TryParseNumber(EvaluateExpression(args[1], variables), out var right))
        {
            return $"[{functionName}: non-numeric argument]";
        }

        return predicate(left, right) ? "true" : "false";
    }

    private static string ResolveVariable(string name, IReadOnlyDictionary<string, string> variables)
    {
        var key = name.StartsWith("variables.", StringComparison.OrdinalIgnoreCase)
            ? name["variables.".Length..]
            : name;

        if (variables.TryGetValue(key, out var resolved))
            return resolved;
        if (TryParseNumber(key, out _))
            return key;
        if (bool.TryParse(key, out _))
            return key.ToLowerInvariant();
        return string.Empty;
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
}
