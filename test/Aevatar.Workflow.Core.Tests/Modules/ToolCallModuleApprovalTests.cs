using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class ToolCallModuleApprovalTests
{
    [Fact]
    public void PendingToolCallApprovalState_ShouldPreserveInputFileRefsForReplay()
    {
        var state = new PendingToolCallApprovalState
        {
            RunId = "run-1",
            StepId = "extract",
            ExecutionId = "exec-1",
            ToolName = "document_extract",
            ToolCallId = "call-1",
            ApprovalRequestId = "approval-1",
            ArgumentsJson = "{}",
        };
        state.InputFileRefs.Add(new WorkflowFileRef
        {
            FileId = "file-approval",
            ArtifactId = "workflow-file://file-approval",
            SourceKind = WorkflowFileSourceKind.ChatInput,
            FileName = "approval.txt",
            MediaType = "text/plain",
        });

        var parsed = PendingToolCallApprovalState.Parser.ParseFrom(state.ToByteArray());

        parsed.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-approval");
    }
}
