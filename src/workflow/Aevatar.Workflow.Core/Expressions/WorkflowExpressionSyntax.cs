namespace Aevatar.Workflow.Core.Expressions;

internal abstract record WorkflowExpressionSegment;

internal sealed record WorkflowTextSegment(string Text) : WorkflowExpressionSegment;

internal sealed record WorkflowInterpolatedExpressionSegment(WorkflowExpressionNode Expression) : WorkflowExpressionSegment;

internal sealed record WorkflowExpressionTemplate(IReadOnlyList<WorkflowExpressionSegment> Segments);

internal abstract record WorkflowExpressionNode;

internal sealed record WorkflowLiteralExpression(string Value) : WorkflowExpressionNode;

internal sealed record WorkflowVariableExpression(string Name) : WorkflowExpressionNode;

internal sealed record WorkflowFunctionCallExpression(string Name, IReadOnlyList<WorkflowExpressionNode> Arguments) : WorkflowExpressionNode;
