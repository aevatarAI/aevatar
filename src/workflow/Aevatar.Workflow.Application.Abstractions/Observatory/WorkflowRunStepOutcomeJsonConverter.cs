using Aevatar.Workflow.Application.Abstractions.Queries;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.Workflow.Application.Abstractions.Observatory;

public sealed class WorkflowRunStepOutcomeJsonConverter : JsonConverter<WorkflowRunStepOutcome>
{
    public override WorkflowRunStepOutcome Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "succeeded" => WorkflowRunStepOutcome.Succeeded,
            "failed" => WorkflowRunStepOutcome.Failed,
            "waiting" => WorkflowRunStepOutcome.Waiting,
            "skipped" => WorkflowRunStepOutcome.Skipped,
            _ => WorkflowRunStepOutcome.Unspecified,
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        WorkflowRunStepOutcome value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            WorkflowRunStepOutcome.Succeeded => "succeeded",
            WorkflowRunStepOutcome.Failed => "failed",
            WorkflowRunStepOutcome.Waiting => "waiting",
            WorkflowRunStepOutcome.Skipped => "skipped",
            _ => "unspecified",
        });
    }
}
