using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatPublicToolReceiptResultTests
{
    [Fact]
    public void BuildDurableReceiptEvidence_WorkflowStart_ShouldKeepOnlyExactTypedProjection()
    {
        const string secret = "start-secret-must-not-persist";
        var receipt = new AgentToolReceipt
        {
            CallId = "command-alpha",
            ToolName = "aevatar_start_workflow",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "scope-workflow:run-alpha",
            MutationStage = AgentToolReceiptMutationStage.ReadModelObserved,
            ResultJson = $$"""
                {
                  "run_id": "scope-workflow:run-alpha",
                  "actor_id": "scope-workflow:run-alpha",
                  "command_id": "command-alpha",
                  "status": "streaming",
                  "mutation_stage": "read_model_observed",
                  "access_token": "{{secret}}",
                  "credential": { "value": "{{secret}}" }
                }
                """,
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.MutationStage.Should().Be(AgentToolReceiptMutationStage.ReadModelObserved);
        durable.ResultJson.Should().NotContain(secret).And.NotContain("access_token");
        using var document = JsonDocument.Parse(durable.ResultJson);
        document.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal(
            "run_id",
            "actor_id",
            "command_id",
            "status",
            "mutation_stage");
        document.RootElement.GetProperty("run_id").GetString().Should()
            .Be("scope-workflow:run-alpha");
        document.RootElement.GetProperty("command_id").GetString().Should().Be("command-alpha");
        document.RootElement.GetProperty("mutation_stage").GetString().Should()
            .Be("read_model_observed");
    }

    [Fact]
    public void BuildDurableReceiptEvidence_WorkflowStartCompleted_ShouldKeepBoundedPartialOutput()
    {
        const string secret = "start-secret-must-not-persist";
        const string partialOutput = "您好，我想预订今晚 7 点，两位，上海海底捞南京西路店。";
        var receipt = new AgentToolReceipt
        {
            CallId = "command-alpha",
            ToolName = "aevatar_start_workflow",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "scope-workflow:run-alpha",
            MutationStage = AgentToolReceiptMutationStage.ReadModelObserved,
            ResultJson = $$"""
                {
                  "run_id": "scope-workflow:run-alpha",
                  "actor_id": "scope-workflow:run-alpha",
                  "command_id": "command-alpha",
                  "status": "Completed",
                  "result": {
                    "run_id": "scope-workflow:run-alpha",
                    "status": "Completed",
                    "state_version": 7,
                    "partial_output": "{{partialOutput}}",
                    "access_token": "{{secret}}"
                  }
                }
                """,
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().NotContain(secret).And.NotContain("access_token");
        using var document = JsonDocument.Parse(durable.ResultJson);
        document.RootElement.GetProperty("status").GetString().Should().Be("completed");
        document.RootElement.GetProperty("state_version").GetInt64().Should().Be(7);
        document.RootElement.GetProperty("partial_output").GetString().Should().Be(partialOutput);
    }

    [Fact]
    public void BuildDurableReceiptEvidence_WorkflowStart_WithMismatchedCommandIdentity_ShouldFailClosed()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "assistant-tool-call-alpha",
            ToolName = "aevatar_start_workflow",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "workflow-run-actor-alpha",
            MutationStage = AgentToolReceiptMutationStage.ReadModelObserved,
            ResultJson = JsonSerializer.Serialize(new
            {
                run_id = "workflow-run-actor-alpha",
                actor_id = "workflow-run-actor-alpha",
                command_id = "workflow-command-alpha",
                status = "streaming",
                mutation_stage = "read_model_observed",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
        NyxIdChatPublicToolReceiptResult.ResolvePresentationResult(durable).Should()
            .Contain("PUBLIC_RECEIPT_UNAVAILABLE");
    }

    [Fact]
    public void BuildDurableReceiptEvidence_WorkflowStart_WithUnboundRunIdentity_ShouldFailClosed()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "assistant-tool-call-alpha",
            ToolName = "aevatar_start_workflow",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "trusted-workflow-run",
            MutationStage = AgentToolReceiptMutationStage.Accepted,
            ResultJson = JsonSerializer.Serialize(new
            {
                run_id = "untrusted-workflow-run",
                actor_id = "untrusted-workflow-run",
                command_id = "assistant-tool-call-alpha",
                status = "accepted",
                mutation_stage = "accepted",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
        NyxIdChatPublicToolReceiptResult.ResolvePresentationResult(durable).Should()
            .Contain("PUBLIC_RECEIPT_UNAVAILABLE");
    }

    [Fact]
    public void BuildDurableReceiptEvidence_ManagedWorkflowStart_ShouldKeepDistinctRunAndActorIds()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "command-alpha",
            ToolName = "aevatar_start_workflow",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "parent-run:workflow_tool:step-alpha:command-alpha",
            MutationStage = AgentToolReceiptMutationStage.Accepted,
            ManagedWorkflowHandoff = new ManagedWorkflowHandoffReceipt
            {
                ParentActorId = "parent-actor",
                ParentRunId = "parent-run",
                ParentStepId = "step-alpha",
                InvocationId = "parent-run:workflow_tool:step-alpha:command-alpha",
                ChildRunId = "parent-run:workflow_tool:step-alpha:command-alpha",
            },
            ResultJson = JsonSerializer.Serialize(new
            {
                run_id = "parent-run:workflow_tool:step-alpha:command-alpha",
                actor_id = "parent-actor",
                command_id = "command-alpha",
                status = "accepted",
                mutation_stage = "accepted",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        using var document = JsonDocument.Parse(durable!.ResultJson);
        document.RootElement.GetProperty("run_id").GetString().Should().Be(receipt.SubjectId);
        document.RootElement.GetProperty("actor_id").GetString().Should().Be("parent-actor");
    }

    [Fact]
    public void BuildDurableReceiptEvidence_DistinctStartIdsWithoutManagedHandoff_ShouldFailClosed()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "command-alpha",
            ToolName = "aevatar_start_workflow",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            MutationStage = AgentToolReceiptMutationStage.Accepted,
            ResultJson = JsonSerializer.Serialize(new
            {
                run_id = "run-alpha",
                actor_id = "actor-alpha",
                command_id = "command-alpha",
                status = "accepted",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
        NyxIdChatPublicToolReceiptResult.ResolvePresentationResult(durable).Should()
            .Contain("PUBLIC_RECEIPT_UNAVAILABLE");
    }

    [Fact]
    public void BuildDurableReceiptEvidence_WorkflowArtifact_ShouldKeepBoundedSafeReport()
    {
        const string secret = "artifact-secret-must-not-persist";
        const string finalOutput = "{\"case\":\"01\",\"success\":true}";
        var finalOutputBytes = Encoding.UTF8.GetBytes(finalOutput);
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "scope-workflow:run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "scope-workflow:run-alpha",
                artifact_actor_id = "scope-workflow:run-alpha",
                root_actor_id = "scope-workflow:run-alpha",
                artifact = "report",
                workflow_name = "sample_workflow",
                status = "Completed",
                success = true,
                state_version = 17,
                command_id = "command-alpha",
                final_output = finalOutput,
                final_output_bytes = finalOutputBytes.Length,
                final_output_sha256 = Convert
                    .ToHexString(SHA256.HashData(finalOutputBytes))
                    .ToLowerInvariant(),
                access_token = secret,
                steps = new[] { new { output = "not part of the public receipt" } },
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().NotContain(secret).And.NotContain("steps");
        using var document = JsonDocument.Parse(durable.ResultJson);
        document.RootElement.GetProperty("workflow_run_id").GetString().Should()
            .Be("scope-workflow:run-alpha");
        document.RootElement.GetProperty("artifact_actor_id").GetString().Should()
            .Be("scope-workflow:run-alpha");
        document.RootElement.GetProperty("artifact").GetString().Should().Be("report");
        document.RootElement.GetProperty("workflow_name").GetString().Should()
            .Be("sample_workflow");
        document.RootElement.GetProperty("status").GetString().Should().Be("completed");
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("state_version").GetInt64().Should().Be(17);
        document.RootElement.GetProperty("command_id").GetString().Should().Be("command-alpha");
        document.RootElement.GetProperty("final_output_bytes").GetInt32().Should()
            .Be(Encoding.UTF8.GetByteCount(finalOutput));
        document.RootElement.GetProperty("final_output_sha256").GetString().Should()
            .Be(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(finalOutput)))
                .ToLowerInvariant());
        document.RootElement.TryGetProperty("final_output", out _).Should().BeFalse();
        durable.ResultJson.Length.Should().BeLessThan(1_024);
    }

    [Fact]
    public void BuildDurableReceiptEvidence_ArtifactWithSensitiveFinalOutput_ShouldOmitOutput()
    {
        const string secret = "nested-secret-must-not-persist";
        var finalOutput = $$"""{"case":"01","access_token":"{{secret}}"}""";
        var finalOutputBytes = Encoding.UTF8.GetBytes(finalOutput);
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "scope-workflow:run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "scope-workflow:run-alpha",
                artifact_actor_id = "scope-workflow:run-alpha",
                root_actor_id = "scope-workflow:run-alpha",
                artifact = "report",
                workflow_name = "sample_workflow",
                status = "completed",
                success = true,
                state_version = 18,
                command_id = "command-alpha",
                final_output = finalOutput,
                final_output_bytes = finalOutputBytes.Length,
                final_output_sha256 = Convert
                    .ToHexString(SHA256.HashData(finalOutputBytes))
                    .ToLowerInvariant(),
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().NotContain(secret).And.NotContain("access_token");
        using var document = JsonDocument.Parse(durable.ResultJson);
        document.RootElement.TryGetProperty("final_output", out _).Should().BeFalse();
        document.RootElement.GetProperty("final_output_sha256").GetString().Should()
            .MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void BuildDurableReceiptEvidence_ArtifactShouldKeepResolvedActorForShortRunId()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "run-alpha",
                artifact_actor_id = "workflow-run-actor-alpha",
                root_actor_id = "workflow-run-actor-alpha",
                artifact = "report",
                workflow_name = "sample_workflow",
                status = "failed",
                success = false,
                state_version = 21,
                command_id = "command-alpha",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        using var document = JsonDocument.Parse(durable!.ResultJson);
        document.RootElement.GetProperty("workflow_run_id").GetString().Should().Be("run-alpha");
        document.RootElement.GetProperty("artifact_actor_id").GetString().Should()
            .Be("workflow-run-actor-alpha");
    }

    [Fact]
    public void BuildDurableReceiptEvidence_ArtifactActorMismatch_ShouldFailClosed()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "run-alpha",
                artifact_actor_id = "caller-supplied-actor",
                root_actor_id = "workflow-run-actor-alpha",
                artifact = "report",
                workflow_name = "sample_workflow",
                status = "failed",
                success = false,
                state_version = 21,
                command_id = "command-alpha",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
        NyxIdChatPublicToolReceiptResult.ResolvePresentationResult(durable).Should()
            .Contain("PUBLIC_RECEIPT_UNAVAILABLE");
    }

    [Fact]
    public void BuildDurableReceiptEvidence_UnknownTool_ShouldNeverPersistRawResult()
    {
        const string secret = "generic-secret-must-not-persist";
        var receipt = new AgentToolReceipt
        {
            CallId = "call-alpha",
            ToolName = "generic_tool",
            Status = AgentToolReceiptStatus.Success,
            ErrorCode = "UNEXPECTED_SUCCESS_ERROR",
            ErrorMessage = secret,
            NyxIdApprovalTerminalOutcome = NyxIdApprovalTerminalOutcome.TimedOut,
            ResultJson = $$"""{"secret":"{{secret}}"}""",
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
        durable.ErrorCode.Should().Be("UNEXPECTED_SUCCESS_ERROR");
        durable.ErrorMessage.Should().BeEmpty();
        durable.NyxIdApprovalTerminalOutcome.Should().Be(NyxIdApprovalTerminalOutcome.TimedOut);
        durable.ToString().Should().NotContain(secret);
    }

    [Fact]
    public void BuildDurableReceiptEvidence_OversizedArtifactOutput_ShouldKeepFullDigestWithoutRawOutput()
    {
        var oversized = JsonSerializer.Serialize(new { payload = new string('x', 5_000) });
        var oversizedBytes = Encoding.UTF8.GetBytes(oversized);
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "scope-workflow:run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "scope-workflow:run-alpha",
                artifact_actor_id = "scope-workflow:run-alpha",
                root_actor_id = "scope-workflow:run-alpha",
                artifact = "report",
                workflow_name = "sample_workflow",
                status = "completed",
                success = true,
                state_version = 19,
                command_id = "command-alpha",
                final_output = $"{oversized[..2_000]}...",
                final_output_bytes = oversizedBytes.Length,
                final_output_sha256 = Convert
                    .ToHexString(SHA256.HashData(oversizedBytes))
                    .ToLowerInvariant(),
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().NotContain(new string('x', 100));
        using var document = JsonDocument.Parse(durable.ResultJson);
        document.RootElement.GetProperty("final_output_bytes").GetInt64().Should()
            .Be(oversizedBytes.Length);
        document.RootElement.GetProperty("final_output_sha256").GetString().Should()
            .Be(Convert.ToHexString(SHA256.HashData(oversizedBytes)).ToLowerInvariant());
        document.RootElement.TryGetProperty("final_output", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildDurableReceiptEvidence_ContradictoryPendingArtifact_ShouldFailClosed()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "scope-workflow:run-alpha",
            ResultJson = """
                {
                  "workflow_run_id": "scope-workflow:run-alpha",
                  "artifact": "report",
                  "status": "pending",
                  "pending": true,
                  "success": true
                }
                """,
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
        NyxIdChatPublicToolReceiptResult.ResolvePresentationResult(durable).Should()
            .Contain("PUBLIC_RECEIPT_UNAVAILABLE");
    }

    [Theory]
    [InlineData("pending", false, true)]
    [InlineData("completed", false, false)]
    [InlineData("completed", true, true)]
    [InlineData("failed", true, false)]
    [InlineData("stopped", true, false)]
    [InlineData("Running", false, false)]
    [InlineData("TimedOut", true, false)]
    [InlineData("TimedOut", false, true)]
    public void BuildDurableReceiptEvidence_ContradictoryArtifactState_ShouldFailClosed(
        string status,
        bool success,
        bool pending)
    {
        var payload = new Dictionary<string, object?>
        {
            ["workflow_run_id"] = "run-alpha",
            ["artifact_actor_id"] = "workflow-run-actor-alpha",
            ["root_actor_id"] = "workflow-run-actor-alpha",
            ["artifact"] = "report",
            ["workflow_name"] = "sample_workflow",
            ["status"] = status,
            ["success"] = success,
            ["pending"] = pending,
            ["state_version"] = 22,
            ["command_id"] = "command-alpha",
            ["final_output_bytes"] = 2,
            ["final_output_sha256"] = new string('a', 64),
        };
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            ResultJson = JsonSerializer.Serialize(payload),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pending", "\"true\"", "true")]
    [InlineData("pending", "null", "true")]
    [InlineData("pending", "1", "true")]
    [InlineData("Running", "\"false\"", "false")]
    [InlineData("Running", "null", "false")]
    [InlineData("completed", "\"true\"", "false")]
    [InlineData("completed", "true", "\"false\"")]
    [InlineData("completed", "true", "null")]
    [InlineData("completed", "true", "0")]
    public void BuildDurableReceiptEvidence_MalformedArtifactBoolean_ShouldFailClosed(
        string status,
        string successJson,
        string pendingJson)
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            ResultJson = $$"""
                {
                  "workflow_run_id": "run-alpha",
                  "artifact_actor_id": "workflow-run-actor-alpha",
                  "root_actor_id": "workflow-run-actor-alpha",
                  "artifact": "report",
                  "workflow_name": "sample_workflow",
                  "status": "{{status}}",
                  "success": {{successJson}},
                  "pending": {{pendingJson}},
                  "state_version": 23,
                  "command_id": "command-alpha",
                  "final_output_bytes": 2,
                  "final_output_sha256": "{{new string('a', 64)}}"
                }
                """,
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
    }

    [Fact]
    public void BuildDurableReceiptEvidence_PendingArtifactWithoutSuccess_ShouldKeepPendingProjection()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "run-alpha",
                artifact_actor_id = "workflow-run-actor-alpha",
                artifact = "report",
                status = "pending",
                pending = true,
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        using var document = JsonDocument.Parse(durable!.ResultJson);
        document.RootElement.GetProperty("status").GetString().Should().Be("pending");
        document.RootElement.GetProperty("pending").GetBoolean().Should().BeTrue();
        document.RootElement.TryGetProperty("success", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("AwaitingToolApproval")]
    [InlineData("WaitingForSignal")]
    public void BuildDurableReceiptEvidence_MaterializedNonTerminalArtifact_ShouldProjectTypedPending(
        string status)
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "run-alpha",
                artifact_actor_id = "workflow-run-actor-alpha",
                root_actor_id = "workflow-run-actor-alpha",
                artifact = "report",
                workflow_name = "sample_workflow",
                status,
                state_version = 24,
                command_id = "workflow-command-alpha",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        using var document = JsonDocument.Parse(durable!.ResultJson);
        document.RootElement.GetProperty("workflow_run_id").GetString().Should().Be("run-alpha");
        document.RootElement.GetProperty("artifact_actor_id").GetString().Should()
            .Be("workflow-run-actor-alpha");
        document.RootElement.GetProperty("status").GetString().Should().Be("pending");
        document.RootElement.GetProperty("pending").GetBoolean().Should().BeTrue();
        document.RootElement.TryGetProperty("success", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildDurableReceiptEvidence_MaterializedNonTerminalArtifactWithFalseSuccess_ShouldProjectTypedPending()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "run-alpha",
                artifact_actor_id = "workflow-run-actor-alpha",
                root_actor_id = "workflow-run-actor-alpha",
                artifact = "report",
                workflow_name = "sample_workflow",
                status = "Running",
                success = false,
                state_version = 24,
                command_id = "workflow-command-alpha",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        using var document = JsonDocument.Parse(durable!.ResultJson);
        document.RootElement.GetProperty("status").GetString().Should().Be("pending");
        document.RootElement.GetProperty("pending").GetBoolean().Should().BeTrue();
        document.RootElement.TryGetProperty("success", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildDurableReceiptEvidence_MaterializedNonTerminalArtifactClaimingSuccess_ShouldFailClosed()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "run-alpha",
                artifact_actor_id = "workflow-run-actor-alpha",
                root_actor_id = "workflow-run-actor-alpha",
                artifact = "report",
                workflow_name = "sample_workflow",
                status = "Running",
                success = true,
                state_version = 24,
                command_id = "workflow-command-alpha",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
    }

    [Fact]
    public void BuildDurableReceiptEvidence_TimedOutArtifact_ShouldProjectTypedTerminalFailure()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "run-alpha",
                artifact_actor_id = "workflow-run-actor-alpha",
                root_actor_id = "workflow-run-actor-alpha",
                artifact = "report",
                workflow_name = "sample_workflow",
                status = "TimedOut",
                success = false,
                state_version = 25,
                command_id = "workflow-command-alpha",
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        using var document = JsonDocument.Parse(durable!.ResultJson);
        document.RootElement.GetProperty("status").GetString().Should().Be("timed_out");
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("state_version").GetInt64().Should().Be(25);
    }

    [Fact]
    public void BuildDurableReceiptEvidence_OversizedRawReceipt_ShouldFailBeforeProjection()
    {
        var receipt = new AgentToolReceipt
        {
            CallId = "artifact-call-alpha",
            ToolName = "aevatar_read_workflow_run_artifact",
            Status = AgentToolReceiptStatus.Success,
            SubjectId = "scope-workflow:run-alpha",
            ResultJson = JsonSerializer.Serialize(new
            {
                workflow_run_id = "scope-workflow:run-alpha",
                artifact = "report",
                status = "pending",
                pending = true,
                ignored = new string('x', 300_000),
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ResultJson.Should().BeEmpty();
    }

    [Fact]
    public void BuildDurableReceiptEvidence_Failure_ShouldExposeOnlyStableCode()
    {
        const string secret = "provider-error-secret-must-not-persist";
        var receipt = new AgentToolReceipt
        {
            CallId = "call-alpha",
            ToolName = "aevatar_start_workflow",
            Status = AgentToolReceiptStatus.Error,
            ErrorCode = "WORKFLOW_START_FAILED",
            ErrorMessage = secret,
            ResultJson = JsonSerializer.Serialize(new
            {
                error = new { message = secret },
            }),
        };

        var durable = NyxIdChatConversationGAgent.BuildDurableReceiptEvidence(receipt);

        durable.Should().NotBeNull();
        durable!.ErrorCode.Should().Be("WORKFLOW_START_FAILED");
        durable.ErrorMessage.Should().BeEmpty();
        durable.ResultJson.Should().BeEmpty();
        var presentation = NyxIdChatPublicToolReceiptResult.ResolvePresentationResult(durable);
        presentation.Should().Contain("WORKFLOW_START_FAILED").And.NotContain(secret);
    }
}
