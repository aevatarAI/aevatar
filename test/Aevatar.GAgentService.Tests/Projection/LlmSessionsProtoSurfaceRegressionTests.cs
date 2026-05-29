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

    private static IEnumerable<string> TopLevelMessageNames(FileDescriptor descriptor) =>
        descriptor.MessageTypes.Select(x => x.Name);
}
