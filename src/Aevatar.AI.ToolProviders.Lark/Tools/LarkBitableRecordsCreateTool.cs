using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkBitableRecordsCreateTool : AgentToolBase<LarkBitableRecordsCreateTool.Parameters>
{
    private readonly ILarkNyxClient _client;

    public LarkBitableRecordsCreateTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_bitable_records_create";

    public override string Description =>
        "Create one Lark Bitable record through Nyx-backed transport. " +
        "Use this when the target app_token, table_id, and record fields object are already known.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var appToken = parameters.AppToken?.Trim();
        if (string.IsNullOrWhiteSpace(appToken))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "app_token is required." });

        var tableId = parameters.TableId?.Trim();
        if (string.IsNullOrWhiteSpace(tableId))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "table_id is required." });

        var fieldsValidation = ValidateFieldsJson(parameters.FieldsJson);
        if (!fieldsValidation.Success)
            return LarkProxyResponseParser.Serialize(new { success = false, error = fieldsValidation.Error });

        var response = await _client.CreateBitableRecordAsync(
            token,
            new LarkBitableRecordCreateRequest(
                AppToken: appToken,
                TableId: tableId,
                FieldsJson: fieldsValidation.FieldsJson!,
                ClientToken: parameters.ClientToken?.Trim()),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error,
                app_token = appToken,
                table_id = tableId,
            });
        }

        var result = LarkProxyResponseParser.ParseBitableRecordCreateSuccess(response);
        if (string.IsNullOrWhiteSpace(result.RecordId))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "missing_record_id",
                app_token = appToken,
                table_id = tableId,
            });
        }

        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            app_token = appToken,
            table_id = tableId,
            record_id = result.RecordId,
            revision = result.Revision,
            fields_json = result.FieldsJson,
        });
    }

    private static (bool Success, string? FieldsJson, string? Error) ValidateFieldsJson(JsonElement? fieldsJson)
    {
        if (fieldsJson is null || fieldsJson.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return (false, null, "fields_json is required.");
        if (fieldsJson.Value.ValueKind != JsonValueKind.Object)
            return (false, null, "fields_json must be a JSON object.");

        return (true, fieldsJson.Value.GetRawText(), null);
    }

    public sealed class Parameters
    {
        public string? AppToken { get; set; }
        public string? TableId { get; set; }
        public JsonElement? FieldsJson { get; set; }
        public string? ClientToken { get; set; }
    }
}
