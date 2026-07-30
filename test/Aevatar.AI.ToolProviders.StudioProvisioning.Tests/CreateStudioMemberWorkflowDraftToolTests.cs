using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Studio.Application.Provisioning;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.ToolProviders.StudioProvisioning.Tests;

public sealed class CreateStudioMemberWorkflowDraftToolTests
{
    private const string ToolName = "aevatar_create_member_workflow_draft";

    [Fact]
    public async Task ExecuteAsync_ShouldUseOwnerScopeAndReturnHonestDraftReceipt()
    {
        var port = new RecordingDraftPort();
        var tool = await DiscoverAsync(port);
        using var _ = PushContext("registration-scope", "owner-scope");

        var output = await tool.ExecuteAsync("""
            {
              "team_id": "team-alpha",
              "display_name": "X Digest",
              "workflow_yaml": "name: x_digest\nsteps: []\n",
              "member_id": "m-alpha",
              "workflow_id": "wf-alpha"
            }
            """);

        port.Request.Should().NotBeNull();
        port.Request!.ScopeId.Should().Be("owner-scope");
        port.Request.TeamId.Should().Be("team-alpha");
        port.Request.DisplayName.Should().Be("X Digest");
        port.Request.WorkflowYaml.Should().Be("name: x_digest\nsteps: []");
        port.Request.MemberId.Should().Be("m-alpha");
        port.Request.WorkflowId.Should().Be("wf-alpha");

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("draft_save_accepted");
        root.GetProperty("runnable").GetBoolean().Should().BeFalse();
        root.GetProperty("binding_status").GetString().Should().Be("not_bound");
        root.GetProperty("member_id").GetString().Should().Be("m-alpha");
        root.GetProperty("workflow_id").GetString().Should().Be("wf-alpha");
        root.GetProperty("studio_url").GetString().Should().Be(
            "/scopes/owner-scope/teams/team-alpha/members/m-alpha/workflow?workflowId=wf-alpha");
        root.GetProperty("command_id").GetString().Should().Be("cmd-alpha");
        root.GetProperty("ack_stage").GetString().Should().Be("accepted");
        root.GetProperty("readiness").GetProperty("stage").GetString()
            .Should().Be("projection_pending");
        root.GetProperty("blockers")[0].GetProperty("code").GetString()
            .Should().Be("NYXID_OPERATION_SELECTION_REQUIRED");
        tool.ApprovalMode.Should().Be(ToolApprovalPolicies.CreateScopedResource);
        tool.IsReadOnly.Should().BeFalse();
        tool.IsDestructive.Should().BeFalse();
        tool.SideEffectKind.Should().Be("studio.workflow_draft.create");
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        schema.RootElement.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString())
            .Should().BeEquivalentTo("team_id", "display_name", "workflow_yaml");
    }

    [Theory]
    [InlineData("scope_id")]
    [InlineData("user_service_id")]
    [InlineData("endpoint_id")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("admission_proof")]
    public async Task ExecuteAsync_WhenAuthorityFieldSupplied_ShouldRejectWithoutCallingPort(string field)
    {
        var port = new RecordingDraftPort();
        var tool = await DiscoverAsync(port);
        using var _ = PushContext("scope-alpha");
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["team_id"] = "team-alpha",
            ["display_name"] = "X Digest",
            ["workflow_yaml"] = "name: x_digest\nsteps: []\n",
            [field] = "forged",
        });

        var output = await tool.ExecuteAsync(arguments);

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be($"Unknown argument: {field}");
        port.Request.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenScopeMissing_ShouldReturnStructuredError()
    {
        var port = new RecordingDraftPort();
        var tool = await DiscoverAsync(port);
        using var _ = PushContext(null);

        var output = await tool.ExecuteAsync("""
            {"team_id":"team-alpha","display_name":"X Digest","workflow_yaml":"name: x_digest\nsteps: []"}
            """);

        ErrorCode(output).Should().Be("caller_scope_unavailable");
        port.Request.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenDraftSaveFails_ShouldPreserveTypedCodeAndReusableMemberId()
    {
        var port = new RecordingDraftPort
        {
            Failure = new StudioMemberWorkflowDraftProvisioningException(
                "workflow_draft_save_failed",
                "The draft save command was not accepted.",
                "m-alpha"),
        };
        var tool = await DiscoverAsync(port);
        using var _ = PushContext("scope-alpha");

        var output = await tool.ExecuteAsync("""
            {"team_id":"team-alpha","display_name":"X Digest","workflow_yaml":"name: x_digest\nsteps: []"}
            """);

        ErrorCode(output).Should().Be("workflow_draft_save_failed");
        using var document = JsonDocument.Parse(output);
        document.RootElement.GetProperty("error").GetProperty("member_id").GetString()
            .Should().Be("m-alpha");
    }

    [Fact]
    public async Task ExecuteAsync_WhenArgumentsAreInvalid_ShouldReturnInvalidArguments()
    {
        var tool = await DiscoverAsync(new RecordingDraftPort
        {
            Failure = new InvalidOperationException("workflow_yaml is invalid."),
        });
        using var _ = PushContext("scope-alpha");

        var output = await tool.ExecuteAsync(JsonSerializer.Serialize(new
        {
            team_id = "team-alpha",
            display_name = "X Digest",
            workflow_yaml = "invalid",
        }));

        ErrorCode(output).Should().Be("invalid_arguments");
        ErrorMessage(output).Should().Be("workflow_yaml is invalid.");
    }

    [Fact]
    public async Task ExecuteAsync_WhenUnexpectedFailureOccurs_ShouldNotLeakExceptionDetails()
    {
        var tool = await DiscoverAsync(new RecordingDraftPort
        {
            Failure = new Exception("secret backend detail"),
        });
        using var _ = PushContext("scope-alpha");

        var output = await tool.ExecuteAsync(JsonSerializer.Serialize(new
        {
            team_id = "team-alpha",
            display_name = "X Digest",
            workflow_yaml = "name: x_digest\nsteps: []",
        }));

        ErrorCode(output).Should().Be("workflow_draft_create_failed");
        output.Should().NotContain("secret backend detail");
        output.Should().NotContain(nameof(Exception));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCanceled_ShouldPropagateCancellation()
    {
        var tool = await DiscoverAsync(new RecordingDraftPort
        {
            Failure = new OperationCanceledException(),
        });
        using var _ = PushContext("scope-alpha");
        var arguments = JsonSerializer.Serialize(new
        {
            team_id = "team-alpha",
            display_name = "X Digest",
            workflow_yaml = "name: x_digest\nsteps: []",
        });

        var act = () => tool.ExecuteAsync(arguments);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<IAgentTool> DiscoverAsync(IStudioMemberWorkflowDraftProvisioningPort port)
    {
        var tools = await new CreateStudioMemberWorkflowDraftToolSource(port).DiscoverToolsAsync();
        return tools.Single(item => item.Name == ToolName);
    }

    private static AgentToolContextScope PushContext(string? scopeId, string? ownerScopeId = null) =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext(scopeId, "owner-alpha", "response-alpha", ownerScopeId),
        });

    private static string? ErrorCode(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static string? ErrorMessage(string output)
    {
        using var document = JsonDocument.Parse(output);
        return document.RootElement.GetProperty("error").GetProperty("message").GetString();
    }

    private sealed class RecordingDraftPort : IStudioMemberWorkflowDraftProvisioningPort
    {
        public StudioMemberWorkflowDraftProvisioningRequest? Request { get; private set; }
        public Exception? Failure { get; init; }

        public Task<StudioMemberWorkflowDraftProvisioningResult> SaveAsync(
            StudioMemberWorkflowDraftProvisioningRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            if (Failure is not null)
                return Task.FromException<StudioMemberWorkflowDraftProvisioningResult>(Failure);

            return Task.FromResult(new StudioMemberWorkflowDraftProvisioningResult(
                "draft_save_accepted",
                false,
                "not_bound",
                request.ScopeId,
                request.TeamId,
                request.MemberId ?? "m-alpha",
                request.WorkflowId ?? "wf-alpha",
                $"/scopes/{request.ScopeId}/teams/{request.TeamId}/members/m-alpha/workflow?workflowId=wf-alpha",
                "cmd-alpha",
                "accepted",
                $"studio-workspace:{request.ScopeId}",
                $"studio-workspace:{request.ScopeId}",
                null,
                DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
                new StudioMemberWorkflowDraftReadiness(
                    false,
                    "projection_pending",
                    "Poll the workflow draft by id."),
                [new StudioMemberWorkflowDraftBlocker(
                    "NYXID_OPERATION_SELECTION_REQUIRED",
                    "Select an exact NyxID operation before binding this draft.")]));
        }
    }
}
