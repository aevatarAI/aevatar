using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class LlmSessionsProtoSurfaceRegressionTests
{
    [Fact]
    public void LlmSessionsProto_ShouldNotExposeDeletedTaskTraceSurface()
    {
        var descriptor = LlmSessionsReflection.Descriptor;

        TopLevelMessageNames(descriptor).Should().NotContain(
            [
                "ResponsesTaskTrace",
                "RecordResponsesTaskRequested",
                "ResponsesTaskRecordedEvent",
            ]);
        descriptor.EnumTypes.Select(x => x.Name).Should().NotContain("ResponsesAgentToolTaskStatus");
        ResponsesAgentToolState.Descriptor.FindFieldByName("task_traces").Should().BeNull();
    }

    [Fact]
    public void ServiceProjectionReadModelsProto_ShouldNotExposeDeletedTaskTraceFields()
    {
        var descriptor = ServiceProjectionReadModelsReflection.Descriptor;

        TopLevelMessageNames(descriptor).Should().NotContain("ResponsesTaskTraceReadModel");
        ResponsesAgentToolStateCurrentStateReadModel.Descriptor
            .FindFieldByName("task_trace_entries")
            .Should()
            .BeNull();
    }

    [Fact]
    public void LlmSessionsProto_ShouldExposeTypedRunStartedSequenceSurface()
    {
        var descriptor = LlmSessionsReflection.Descriptor;

        TopLevelMessageNames(descriptor).Should().Contain("LlmRunStartedEvent");
        TopLevelMessageNames(descriptor).Should().NotContain("LlmRunRecordAppliedEvent");
        LlmRunStartedEvent.Descriptor.FindFieldByName("sequence").Should().NotBeNull();
        LlmStreamChunkObserved.Descriptor.FindFieldByName("sequence").Should().NotBeNull();
        LlmToolCallObserved.Descriptor.FindFieldByName("sequence").Should().NotBeNull();
        LlmRunCompleted.Descriptor.FindFieldByName("sequence").Should().NotBeNull();
        LlmRunFailed.Descriptor.FindFieldByName("sequence").Should().NotBeNull();
        LlmRunCancelled.Descriptor.FindFieldByName("sequence").Should().NotBeNull();
        LlmSessionRunScope.Descriptor.FindFieldByName("last_applied_sequence").Should().NotBeNull();
    }

    [Fact]
    public void LlmSessionsProto_ShouldExposeActorOwnedCancelAndBatchedForwardedToolRecords()
    {
        var descriptor = LlmSessionsReflection.Descriptor;

        TopLevelMessageNames(descriptor).Should().Contain("CancelLlmRunRequested");
        CancelLlmRunRequested.Descriptor.FindFieldByName("run_id").Should().NotBeNull();
        LlmRunCompleted.Descriptor.FindFieldByName("forwarded_tool_call_records").Should().NotBeNull();
    }

    private static IEnumerable<string> TopLevelMessageNames(FileDescriptor descriptor) =>
        descriptor.MessageTypes.Select(x => x.Name);
}
