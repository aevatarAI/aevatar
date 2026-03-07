namespace Aevatar.Workflow.Core.Expressions;

/// <summary>
/// Thin facade over expression parse + runtime evaluate phases.
/// </summary>
public sealed class WorkflowExpressionEvaluator
{
    private readonly WorkflowExpressionParser _parser = new();
    private readonly WorkflowExpressionRuntimeEvaluator _runtimeEvaluator = new();

    public string Evaluate(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (template == null)
            return null!;

        return _runtimeEvaluator.EvaluateTemplate(_parser.ParseTemplate(template), variables);
    }

    public string EvaluateExpression(string expression, IReadOnlyDictionary<string, string> variables) =>
        _runtimeEvaluator.EvaluateExpression(_parser.ParseExpression(expression), variables);
}
