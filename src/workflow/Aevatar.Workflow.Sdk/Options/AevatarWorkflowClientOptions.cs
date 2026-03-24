using System.Text.Json;
using Aevatar.Workflow.Sdk.Internal;

namespace Aevatar.Workflow.Sdk.Options;

public sealed class AevatarWorkflowClientOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5100";

    public IDictionary<string, string> DefaultHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public JsonSerializerOptions JsonSerializerOptions { get; set; } =
        WorkflowSdkJson.CreateSerializerOptions();
}
