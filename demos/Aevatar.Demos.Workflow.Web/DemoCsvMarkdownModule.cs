using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Demos.Workflow.Web;

public sealed class DemoCsvMarkdownModule
    : IEventModule<IWorkflowExecutionContext>,
        IEventModule<IEventHandlerContext>
{
    public string Name => "demo_csv_markdown";
    public int Priority => -10;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true ||
        envelope.Payload?.Is(ChatRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null || !payload.Is(StepRequestEvent.Descriptor))
            return;

        var request = payload.Unpack<StepRequestEvent>();
        if (!IsSupportedStepType(request.StepType))
            return;

        var input = request.Input ?? string.Empty;
        var delimiter = request.Parameters.GetValueOrDefault("delimiter", ",");
        var hasHeader = !string.Equals(request.Parameters.GetValueOrDefault("has_header", "true"), "false",
            StringComparison.OrdinalIgnoreCase);

        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = ConvertCsvToMarkdown(input, delimiter, hasHeader),
        }, EventDirection.Self, ct);
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null || !payload.Is(ChatRequestEvent.Descriptor))
            return;

        // Only intercept role-level ChatRequest events. Root workflow actor ChatRequest
        // must continue into WorkflowGAgent -> StartWorkflow flow.
        if (ctx.AgentId.IndexOf(':', StringComparison.Ordinal) < 0)
            return;

        var chatRequest = payload.Unpack<ChatRequestEvent>();
        var table = ConvertCsvToMarkdown(chatRequest.Prompt ?? string.Empty, ",", hasHeader: true);
        var response = new ChatResponseEvent
        {
            SessionId = chatRequest.SessionId ?? string.Empty,
            Content = $"[demo_csv_markdown role module]\n{table}",
        };

        await ctx.PublishAsync(response, EventDirection.Up, ct);

        // Replace payload to prevent the default RoleGAgent ChatRequest handler from invoking LLM.
        envelope.Payload = Any.Pack(response);
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
