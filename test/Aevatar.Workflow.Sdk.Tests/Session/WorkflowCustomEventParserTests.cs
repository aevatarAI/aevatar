using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Sdk.Contracts;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Sdk.Tests.Session;

public sealed class WorkflowCustomEventParserTests
{
    [Fact]
    public void TryParseRunContext_ShouldParseCamelCaseAndPascalCase()
    {
        var camel = CustomFrame(WorkflowCustomEventNames.RunContext, new WorkflowRunContextPayload
        {
            ActorId = "actor-c",
            WorkflowName = "auto",
            CommandId = "cmd-c",
        });
        var pascal = CustomFrame(WorkflowCustomEventNames.RunContext, new WorkflowRunContextPayload
        {
            ActorId = "actor-p",
            WorkflowName = "manual",
            CommandId = "cmd-p",
        });

        WorkflowCustomEventParser.TryParseRunContext(camel, out var camelData).Should().BeTrue();
        WorkflowCustomEventParser.TryParseRunContext(pascal, out var pascalData).Should().BeTrue();

        camelData.ActorId.Should().Be("actor-c");
        camelData.WorkflowName.Should().Be("auto");
        camelData.CommandId.Should().Be("cmd-c");
        pascalData.ActorId.Should().Be("actor-p");
        pascalData.WorkflowName.Should().Be("manual");
        pascalData.CommandId.Should().Be("cmd-p");
    }

    [Fact]
    public void TryParseWaitingSignal_ShouldReturnTypedPayload()
    {
        var frame = CustomFrame(WorkflowCustomEventNames.WaitingSignal, new WorkflowWaitingSignalCustomPayload
        {
            RunId = "run-1",
            StepId = "wait-1",
            SignalName = "continue",
            TimeoutMs = 30000,
        });

        var ok = WorkflowCustomEventParser.TryParseWaitingSignal(frame, out var data);

        ok.Should().BeTrue();
        data.RunId.Should().Be("run-1");
        data.StepId.Should().Be("wait-1");
        data.SignalName.Should().Be("continue");
        data.TimeoutMs.Should().Be(30000);
    }

    [Fact]
    public void TryParseStepCompleted_ShouldReturnTypedControlFields()
    {
        var frame = CustomFrame(WorkflowCustomEventNames.StepCompleted, new WorkflowStepCompletedCustomPayload
        {
            RunId = "run-1",
            StepId = "branch-1",
            Success = true,
            Output = "done",
            Annotations = { { "source", "tests" } },
            NextStepId = "publish",
            BranchKey = "approved",
            AssignedVariable = "result",
            AssignedValue = "done",
        });

        var ok = WorkflowCustomEventParser.TryParseStepCompleted(frame, out var data);

        ok.Should().BeTrue();
        data.RunId.Should().Be("run-1");
        data.StepId.Should().Be("branch-1");
        data.Annotations.Should().ContainKey("source").WhoseValue.Should().Be("tests");
        data.NextStepId.Should().Be("publish");
        data.BranchKey.Should().Be("approved");
        data.AssignedVariable.Should().Be("result");
        data.AssignedValue.Should().Be("done");
    }

    [Fact]
    public void TryParseHumanInputRequest_WhenEventNameMismatch_ShouldReturnFalse()
    {
        var frame = CustomFrame(WorkflowCustomEventNames.StepRequest, new WorkflowStepRequestCustomPayload
        {
            RunId = "run-1",
            StepId = "s1",
        });

        var ok = WorkflowCustomEventParser.TryParseHumanInputRequest(frame, out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParseHumanInputRequest_ShouldReturnTypedSecureInputWithoutMetadataMirror()
    {
        var frame = CustomFrame(WorkflowCustomEventNames.HumanInputRequest, new WorkflowHumanInputRequestCustomPayload
        {
            RunId = "run-1",
            StepId = "approve",
            SuspensionType = "secure_input",
            Prompt = "approve?",
            TimeoutSeconds = 30,
            VariableName = "decision",
            Secure = true,
            RedactedOutput = "[captured]",
            Metadata = { { "source", "test" } },
        });

        var ok = WorkflowCustomEventParser.TryParseHumanInputRequest(frame, out var data);

        ok.Should().BeTrue();
        data.VariableName.Should().Be("decision");
        data.Secure.Should().BeTrue();
        data.RedactedOutput.Should().Be("[captured]");
        data.Metadata.Should().ContainKey("source").WhoseValue.Should().Be("test");
        data.Metadata.Should().NotContainKey("variable");
        data.Metadata.Should().NotContainKey("secure");
        data.Metadata.Should().NotContainKey("input_mode");
        data.Metadata.Should().NotContainKey("redacted_output");
    }

    [Fact]
    public void TryParseHumanInputRequest_ShouldPreferTypedSecureInputOverLegacyMetadata()
    {
        var frame = CustomFrame(WorkflowCustomEventNames.HumanInputRequest, new WorkflowHumanInputRequestCustomPayload
        {
            RunId = "run-1",
            StepId = "approve",
            SuspensionType = "secure_input",
            Prompt = "approve?",
            TimeoutSeconds = 30,
            VariableName = "decision",
            Secure = true,
            RedactedOutput = "[captured]",
            Metadata =
            {
                { "variable", "legacy_decision" },
                { "secure", "false" },
                { "input_mode", "password" },
                { "redacted_output", "[legacy captured]" },
                { "source", "test" },
            },
        });

        var ok = WorkflowCustomEventParser.TryParseHumanInputRequest(frame, out var data);

        ok.Should().BeTrue();
        data.VariableName.Should().Be("decision");
        data.Secure.Should().BeTrue();
        data.RedactedOutput.Should().Be("[captured]");
        data.Metadata.Should().ContainKey("source").WhoseValue.Should().Be("test");
        data.Metadata.Should().NotContainKey("variable");
        data.Metadata.Should().NotContainKey("secure");
        data.Metadata.Should().NotContainKey("input_mode");
        data.Metadata.Should().NotContainKey("redacted_output");
    }

    [Fact]
    public void TryParseHumanInputRequest_ShouldFallbackLegacySecureInputMetadata()
    {
        var frame = CustomFrame(WorkflowCustomEventNames.HumanInputRequest, new WorkflowHumanInputRequestCustomPayload
        {
            RunId = "run-1",
            StepId = "approve",
            SuspensionType = "secure_input",
            Prompt = "approve?",
            TimeoutSeconds = 30,
            Metadata =
            {
                { "variable", "decision" },
                { "secure", "true" },
                { "input_mode", "password" },
                { "redacted_output", "[legacy captured]" },
            },
        });

        var ok = WorkflowCustomEventParser.TryParseHumanInputRequest(frame, out var data);

        ok.Should().BeTrue();
        data.VariableName.Should().BeEmpty();
        data.Secure.Should().BeFalse();
        data.RedactedOutput.Should().BeEmpty();
        data.Metadata.Should().NotContainKey("variable");
        data.Metadata.Should().NotContainKey("secure");
        data.Metadata.Should().NotContainKey("input_mode");
        data.Metadata.Should().NotContainKey("redacted_output");
    }

    [Fact]
    public void TryParseToolApprovalPending_ShouldReturnTypedContinuationFields()
    {
        var frame = CustomFrame(WorkflowCustomEventNames.ToolApprovalPending, new WorkflowToolApprovalSuspensionCustomPayload
        {
            RunId = "run-tool",
            StepId = "step-tool",
            ExecutionId = "exec-tool",
            ToolName = "dangerous_tool",
            ToolCallId = "call-tool",
            ApprovalRequestId = "approval-tool",
            ArgumentsJson = """{"danger":true}""",
        });

        var ok = WorkflowCustomEventParser.TryParseToolApprovalPending(frame, out var data);

        ok.Should().BeTrue();
        data.RunId.Should().Be("run-tool");
        data.StepId.Should().Be("step-tool");
        data.ExecutionId.Should().Be("exec-tool");
        data.ToolName.Should().Be("dangerous_tool");
        data.ToolCallId.Should().Be("call-tool");
        data.ApprovalRequestId.Should().Be("approval-tool");
        data.ArgumentsJson.Should().Be("""{"danger":true}""");
    }

    [Fact]
    public void TryParseSignalBuffered_ShouldReturnTypedPayload()
    {
        var frame = CustomFrame(WorkflowCustomEventNames.SignalBuffered, new WorkflowSignalBufferedCustomPayload
        {
            RunId = "run-2",
            StepId = "wait-2",
            SignalName = "continue",
            Payload = "ok",
            ReceivedAtUnixTimeMs = 1710000000000,
        });

        var ok = WorkflowCustomEventParser.TryParseSignalBuffered(frame, out var data);

        ok.Should().BeTrue();
        data.RunId.Should().Be("run-2");
        data.StepId.Should().Be("wait-2");
        data.SignalName.Should().Be("continue");
        data.Payload.Should().Be("ok");
        data.ReceivedAtUnixTimeMs.Should().Be(1710000000000);
    }

    private static WorkflowRunEventEnvelope CustomFrame<TPayload>(string name, TPayload payload)
        where TPayload : class, IMessage =>
        new()
        {
            Custom = new WorkflowCustomEventPayload
            {
                Name = name,
                Payload = Any.Pack(payload),
            },
        };
}
