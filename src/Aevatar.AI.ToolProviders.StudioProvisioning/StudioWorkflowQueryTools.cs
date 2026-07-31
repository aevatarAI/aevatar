using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal sealed class ListStudioWorkflowsTool : IStudioReadOnlyReceiptTool
{
    private static readonly JsonSerializerOptions s_jsonOptions = StudioQueryToolJson.Options;
    private readonly IStudioMemberQueryPort _memberQueryPort;

    public ListStudioWorkflowsTool(IStudioMemberQueryPort memberQueryPort)
    {
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
    }

    public string Name => "aevatar_list_workflows";

    public string Description =>
        "List Team-owned workflow members in the caller's current Aevatar workspace. " +
        "Optionally supply team_id, page_size, and page_token; do not provide scope_id because scope is taken from the session context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "team_id": {
              "type": "string",
              "description": "Optional Studio team id to filter workflow members by Team ownership."
            },
            "page_size": {
              "type": "integer",
              "description": "Optional maximum number of member read-model rows to inspect."
            },
            "page_token": {
              "type": "string",
              "description": "Optional continuation token returned by a previous list call."
            }
          }
        }
        """;

    public bool IsReadOnly => true;

    public bool IsDestructive => false;

    public IReadOnlyList<StudioQueryToolJson.ResultPropertyRequirement> ResultRequirements { get; } = new[]
    {
        StudioQueryToolJson.StringProperty("scope_id"),
        StudioQueryToolJson.ArrayProperty("workflows"),
    };

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var scopeId = StudioToolScopeResolver.ResolveOwnerScopeOrCallerScope();
        if (scopeId is null)
        {
            return StudioQueryToolJson.ErrorJson(
                "caller_scope_unavailable",
                "scope_id is required in AgentToolRequestContext. The workspace workflow query tool uses the caller scope from the tool execution context.");
        }

        ListStudioWorkflowsArguments? args;
        try
        {
            var unknownArgument = StudioQueryToolJson.FindUnknownArgument(
                argumentsJson,
                ["team_id", "page_size", "page_token"]);
            if (unknownArgument is not null)
                return StudioQueryToolJson.ErrorJson("invalid_arguments", $"Unknown argument: {unknownArgument}");

            args = StudioQueryToolJson.Deserialize<ListStudioWorkflowsArguments>(argumentsJson);
        }
        catch (JsonException ex)
        {
            return StudioQueryToolJson.ErrorJson(
                "invalid_arguments",
                $"Could not parse tool arguments: {ex.Message}");
        }

        try
        {
            var page = args is null
                || (args.PageSize is null
                    && string.IsNullOrWhiteSpace(args.PageToken)
                    && string.IsNullOrWhiteSpace(args.TeamId))
                ? null
                : new StudioMemberRosterPageRequest(
                    args.PageSize,
                    StudioQueryToolJson.Normalize(args.PageToken),
                    StudioQueryToolJson.Normalize(args.TeamId));
            var result = await _memberQueryPort.ListAsync(scopeId, page, ct);
            var workflows = result.Members
                .Where(member =>
                    string.Equals(member.ScopeId, scopeId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(member.TeamId)
                    && string.Equals(
                        member.ImplementationKind,
                        MemberImplementationKindNames.Workflow,
                        StringComparison.Ordinal))
                .Select(StudioWorkflowResultJson.From)
                .ToArray();

            return JsonSerializer.Serialize(
                new ListStudioWorkflowsResultJson(scopeId, workflows, result.NextPageToken),
                s_jsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StudioQueryToolJson.ErrorJson(
                "workflow_query_failed",
                $"Studio workflow query failed: {ex.GetType().Name}");
        }
    }

    private sealed record ListStudioWorkflowsArguments(
        [property: JsonPropertyName("team_id")] string? TeamId,
        [property: JsonPropertyName("page_size")] int? PageSize,
        [property: JsonPropertyName("page_token")] string? PageToken);

    private sealed record ListStudioWorkflowsResultJson(
        string ScopeId,
        IReadOnlyList<StudioWorkflowResultJson> Workflows,
        string? NextPageToken);
}

internal sealed record StudioWorkflowResultJson(
    string ScopeId,
    string TeamId,
    string MemberId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? WorkflowId,
    string PublishedServiceId,
    string DisplayName,
    string Description,
    string LifecycleStage,
    string? WorkflowRevision,
    string? LastBoundRevisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string WorkflowUrl)
{
    public static StudioWorkflowResultJson From(StudioMemberSummaryResponse member)
    {
        var teamId = member.TeamId!;
        return new StudioWorkflowResultJson(
            member.ScopeId,
            teamId,
            member.MemberId,
            member.ImplementationRef?.WorkflowId,
            member.PublishedServiceId,
            member.DisplayName,
            member.Description,
            member.LifecycleStage,
            member.ImplementationRef?.WorkflowRevision,
            member.LastBoundRevisionId,
            member.CreatedAt,
            member.UpdatedAt,
            $"/scopes/{Uri.EscapeDataString(member.ScopeId)}/teams/{Uri.EscapeDataString(teamId)}/members/{Uri.EscapeDataString(member.MemberId)}/workflow");
    }
}
