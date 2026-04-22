using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkSheetsAppendRowsTool : AgentToolBase<LarkSheetsAppendRowsTool.Parameters>
{
    private readonly ILarkNyxClient _client;

    public LarkSheetsAppendRowsTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_sheets_append_rows";

    public override string Description =>
        "Append rows to a known Lark spreadsheet through Nyx-backed transport. " +
        "Use this when the target spreadsheet token or URL is already known and you need to add tabular records.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var spreadsheetToken = ResolveSpreadsheetToken(parameters);
        if (string.IsNullOrWhiteSpace(spreadsheetToken))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "One of spreadsheet_token or spreadsheet_url is required.",
            });
        }

        var rows = parameters.Rows?
            .Where(row => row is { Count: > 0 })
            .Select(row => (IReadOnlyList<string?>)row.Select(cell => cell?.Trim()).ToArray())
            .ToArray();
        if (rows is not { Length: > 0 })
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "rows must contain at least one non-empty row.",
            });
        }

        if (!LarkSheetsRangeHelper.TryResolveAppendRange(parameters.SheetId, parameters.Range, out var range, out var rangeError))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = rangeError,
            });
        }

        var response = await _client.AppendSheetRowsAsync(
            token,
            new LarkSheetAppendRowsRequest(
                SpreadsheetToken: spreadsheetToken,
                Range: range!,
                Rows: rows),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error,
                spreadsheet_token = spreadsheetToken,
                range,
            });
        }

        var result = LarkProxyResponseParser.ParseSheetAppendSuccess(response);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            spreadsheet_token = spreadsheetToken,
            range,
            updated_range = result.UpdatedRange,
            table_range = result.TableRange,
            updated_rows = result.UpdatedRows,
            updated_columns = result.UpdatedColumns,
            updated_cells = result.UpdatedCells,
        });
    }

    private static string? ResolveSpreadsheetToken(Parameters parameters)
    {
        if (!string.IsNullOrWhiteSpace(parameters.SpreadsheetToken))
            return parameters.SpreadsheetToken.Trim();
        if (!string.IsNullOrWhiteSpace(parameters.SpreadsheetUrl))
            return LarkSheetsRangeHelper.ExtractSpreadsheetToken(parameters.SpreadsheetUrl);
        return null;
    }

    public sealed class Parameters
    {
        public string? SpreadsheetToken { get; set; }
        public string? SpreadsheetUrl { get; set; }
        public string? SheetId { get; set; }
        public string? Range { get; set; }
        public List<List<string?>>? Rows { get; set; }
    }
}
