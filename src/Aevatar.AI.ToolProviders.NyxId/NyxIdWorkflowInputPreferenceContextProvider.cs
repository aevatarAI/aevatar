using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdWorkflowInputPreferenceContextProvider(
    NyxIdConnectedServiceToolSource connectedServiceToolSource,
    IAgentToolExecutionPort toolExecutionPort)
    : IWorkflowInputPreferenceContextProvider
{
    private const string ReadProjectionKind = "connected_service_read_projection";

    public async ValueTask<WorkflowInputPreferenceContext> ReadAsync(
        WorkflowInputPreferenceContextRequest request,
        CancellationToken ct = default)
    {
        var tools = await connectedServiceToolSource.DiscoverToolsAsync(ct).ConfigureAwait(false);
        var sources = new List<WorkflowInputPreferenceContextSource>();
        foreach (var tool in tools.Where(IsPreferenceContextTool))
        {
            var outcome = await toolExecutionPort.ExecuteAsync(
                    new AgentToolExecutionRequest(
                        tool,
                        "{}",
                        BuildExecutionContext(request, tool),
                        AgentToolApprovalContinuationMode.None,
                        null),
                    ct)
                .ConfigureAwait(false);
            if (outcome.Kind is (AgentToolExecutionOutcomeKind.Executed or
                    AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete) &&
                outcome.Receipt.Status == AgentToolReceiptStatus.Success &&
                TryReadSucceededProjectionData(outcome.ResultJson, out var dataJson))
            {
                var admission = ((IAgentToolOperationAdmissionOwner)tool).OperationAdmission;
                sources.Add(new WorkflowInputPreferenceContextSource(
                    tool.Name,
                    ResolveOperationId(admission),
                    admission.PathTemplate,
                    dataJson));
            }
        }

        return sources.Count == 0
            ? WorkflowInputPreferenceContext.Empty
            : new WorkflowInputPreferenceContext(sources);
    }

    private static AgentToolExecutionContext BuildExecutionContext(
        WorkflowInputPreferenceContextRequest request,
        IAgentTool tool)
    {
        var context = request.ToolContext ?? AgentToolExecutionContext.Empty;
        var requestId = Normalize(context.Request.RequestId) ??
                        $"workflow-preference-context:{Normalize(request.WorkflowId) ?? "unknown"}";
        var callId = Normalize(context.Request.CallId) ??
                     $"{requestId}:{tool.Name}";
        var owner = context.ExecutionOwner.Kind == AgentToolExecutionOwnerKind.Unspecified ||
                    string.IsNullOrWhiteSpace(context.ExecutionOwner.OwnerId)
            ? AgentToolExecutionOwners.HostService(nameof(NyxIdWorkflowInputPreferenceContextProvider))
            : context.ExecutionOwner;
        return context with
        {
            Request = context.Request with
            {
                RequestId = requestId,
                CallId = callId,
            },
            ExecutionOwner = owner,
        };
    }

    private static bool IsPreferenceContextTool(IAgentTool tool)
    {
        if (tool is not IAgentToolOperationAdmissionOwner owner || !tool.IsReadOnly)
            return false;

        var admission = owner.OperationAdmission;
        if (admission.ExecutionPolicy.Risk != AgentToolOperationRisk.ReadOnly ||
            admission.Parameters.Any(static parameter => parameter.Required) ||
            admission.RequestBody?.Required == true)
        {
            return false;
        }

        return MatchesPreferenceContextSemantics(
            tool.Name,
            tool.Description,
            admission.PathTemplate,
            ResolveOperationId(admission));
    }

    private static bool MatchesPreferenceContextSemantics(params string?[] values)
    {
        var tokens = Tokenize(values);
        if (tokens.Contains("preference"))
            return true;

        if (tokens.Contains("dining") &&
            (tokens.Contains("profile") || tokens.Contains("context")))
        {
            return true;
        }

        return tokens.Contains("profile") &&
               tokens.Contains("context") &&
               !tokens.Contains("agent");
    }

    private static HashSet<string> Tokenize(IEnumerable<string?> values)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var builder = new System.Text.StringBuilder();
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (char.IsUpper(ch) && builder.Length > 0)
                    {
                        var previous = builder[builder.Length - 1];
                        if (char.IsLower(previous) || char.IsDigit(previous))
                            AddToken(builder, tokens);
                    }

                    builder.Append(char.ToLowerInvariant(ch));
                    continue;
                }

                AddToken(builder, tokens);
            }

            AddToken(builder, tokens);
        }

        return tokens;
    }

    private static void AddToken(System.Text.StringBuilder builder, HashSet<string> tokens)
    {
        if (builder.Length == 0)
            return;

        var token = builder.ToString();
        builder.Clear();
        tokens.Add(token);
        if (token.Length > 3 && token.EndsWith('s'))
            tokens.Add(token[..^1]);
    }

    private static bool TryReadSucceededProjectionData(string resultJson, out string dataJson)
    {
        dataJson = string.Empty;
        if (string.IsNullOrWhiteSpace(resultJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("kind", out var kind) ||
                kind.GetString() != ReadProjectionKind ||
                !root.TryGetProperty("status", out var status) ||
                status.GetString() != "succeeded" ||
                root.TryGetProperty("instructions_allowed", out var instructionsAllowed) &&
                instructionsAllowed.ValueKind == JsonValueKind.True ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            dataJson = data.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ResolveOperationId(AgentToolOperationAdmission admission) =>
        admission.Identity is AgentToolOperationIdentity.PublishedEndpoint published
            ? published.EndpointId
            : string.Empty;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
