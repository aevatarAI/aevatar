using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;

namespace Aevatar.AI.ToolProviders.Web.Tools;

public sealed class ReimbursementEvidenceTool : IAgentTool
{
    public const string ToolName = "reimbursement_evidence_commit";

    public string Name => ToolName;

    public string Description =>
        "Commit normalized reimbursement evidence before filing. Include every pasted invoice, " +
        "the retained ordinals, and exact duplicate relationships. The conversation actor " +
        "validates the evidence and this tool never writes to the provider.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "source_input_request_id": { "type": "string" },
            "expense_category": { "type": "string" },
            "cost_center": { "type": "string" },
            "reimbursement_currency_instruction": { "type": "string" },
            "guarded_tool_name": { "type": "string" },
            "source_invoices": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "source_ordinal": { "type": "integer", "minimum": 1 },
                  "vendor": { "type": "string" },
                  "invoice_number": { "type": "string" },
                  "invoice_date": { "type": "string", "format": "date" },
                  "amount": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "currency_code": { "type": "string", "minLength": 3, "maxLength": 3 },
                      "minor_units": { "type": "integer", "minimum": 1 },
                      "fraction_digits": { "type": "integer", "minimum": 0, "maximum": 6 }
                    },
                    "required": ["currency_code", "minor_units", "fraction_digits"]
                  }
                },
                "required": ["source_ordinal", "vendor", "invoice_number", "invoice_date", "amount"]
              }
            },
            "retained_source_ordinals": {
              "type": "array",
              "minItems": 1,
              "items": { "type": "integer", "minimum": 1 }
            },
            "duplicate_invoices": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "duplicate_source_ordinal": { "type": "integer", "minimum": 1 },
                  "retained_source_ordinal": { "type": "integer", "minimum": 1 }
                },
                "required": ["duplicate_source_ordinal", "retained_source_ordinal"]
              }
            }
          },
          "required": ["source_input_request_id", "expense_category", "cost_center", "reimbursement_currency_instruction", "guarded_tool_name", "source_invoices", "retained_source_ordinals", "duplicate_invoices"]
        }
        """;

    public ToolPresentationDescriptor Presentation => ToolPresentationDescriptors.BuiltIn(
        Name,
        "Normalize reimbursement",
        Description);

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult("{\"error\":\"reimbursement_evidence_requires_conversation_actor\"}");
}

public sealed class CandidateScreeningEvidenceTool : IAgentTool
{
    public const string ToolName = "candidate_screening_evidence_commit";

    public string Name => ToolName;

    public string Description =>
        "Commit a candidate score against the user's rubric before evaluating the threshold. " +
        "The conversation actor validates criterion bounds and totals. This tool never writes " +
        "to the candidate tracker.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "source_input_request_id": { "type": "string" },
            "candidate_name": { "type": "string" },
            "role_title": { "type": "string" },
            "rubric": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "criterion_id": { "type": "string" },
                  "title": { "type": "string" },
                  "maximum_points": { "type": "integer", "minimum": 1, "maximum": 100 }
                },
                "required": ["criterion_id", "title", "maximum_points"]
              }
            },
            "scores": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "criterion_id": { "type": "string" },
                  "awarded_points": { "type": "integer", "minimum": 0, "maximum": 100 },
                  "evidence": { "type": "string" }
                },
                "required": ["criterion_id", "awarded_points", "evidence"]
              }
            },
            "total_score": { "type": "integer", "minimum": 0, "maximum": 100 },
            "tracker_table": { "type": "string" },
            "tracker_table_id": { "type": "string" },
            "stage": { "type": "string" },
            "guarded_tool_name": { "type": "string" }
          },
          "required": ["source_input_request_id", "candidate_name", "role_title", "rubric", "scores", "total_score", "tracker_table", "tracker_table_id", "stage", "guarded_tool_name"]
        }
        """;

    public ToolPresentationDescriptor Presentation => ToolPresentationDescriptors.BuiltIn(
        Name,
        "Score candidate",
        Description);

    public bool IsReadOnly => true;

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult("{\"error\":\"candidate_evidence_requires_conversation_actor\"}");
}
