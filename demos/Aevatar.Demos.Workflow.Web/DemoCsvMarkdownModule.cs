using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoCsvMarkdownModule : IWorkflowPrimitiveHandler
{
    public string Name => "demo_csv_markdown";

    public Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
        if (!IsSupportedStepType(request.StepType))
            return Task.CompletedTask;

        var input = request.Input ?? string.Empty;
        var delimiter = request.Parameters.GetValueOrDefault("delimiter", ",");
        var hasHeader = !string.Equals(request.Parameters.GetValueOrDefault("has_header", "true"), "false",
            StringComparison.OrdinalIgnoreCase);

        return ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = ConvertCsvToMarkdown(input, delimiter, hasHeader),
        }, EventDirection.Self, ct);
    }

    private static bool IsSupportedStepType(string stepType) =>
        string.Equals(stepType, "demo_csv_markdown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stepType, "demo_table", StringComparison.OrdinalIgnoreCase);

    private static string ConvertCsvToMarkdown(string input, string delimiter, bool hasHeader)
    {
        var rows = input
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(delimiter, StringSplitOptions.None).Select(cell => cell.Trim()).ToArray())
            .Where(cells => cells.Length > 0)
            .ToList();

        if (rows.Count == 0)
            return string.Empty;

        var maxColumns = rows.Max(r => r.Length);
        var header = hasHeader
            ? NormalizeRow(rows[0], maxColumns)
            : Enumerable.Range(1, maxColumns).Select(i => $"col_{i}").ToArray();
        var bodyRows = hasHeader ? rows.Skip(1).ToList() : rows;

        var sb = new StringBuilder();
        sb.AppendLine($"| {string.Join(" | ", header)} |");
        sb.AppendLine($"| {string.Join(" | ", header.Select(_ => "---"))} |");
        foreach (var row in bodyRows)
        {
            var normalized = NormalizeRow(row, maxColumns);
            sb.AppendLine($"| {string.Join(" | ", normalized)} |");
        }

        return sb.ToString().TrimEnd();
    }

    private static string[] NormalizeRow(string[] row, int maxColumns)
    {
        if (row.Length == maxColumns)
            return row;

        var buffer = new string[maxColumns];
        for (var i = 0; i < maxColumns; i++)
            buffer[i] = i < row.Length ? row[i] : string.Empty;
        return buffer;
    }
}
