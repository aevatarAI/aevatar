using System.Reflection;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Core.GAgents;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ResponsesAgentToolStateCSharpSurfaceRegressionTests
{
    [Fact]
    public void ResponsesAgentToolStateCSharpSurface_ShouldNotExposeDeletedTaskMembers()
    {
        typeof(ResponsesAgentToolStateSnapshot)
            .GetProperty("Tasks")
            .Should()
            .BeNull();

        Type.GetType(
                "Aevatar.GAgentService.Abstractions.Queries.ResponsesTaskTraceSnapshot, " +
                "Aevatar.GAgentService.Abstractions")
            .Should()
            .BeNull();

        typeof(ResponsesAgentToolStateGAgent)
            .GetMethod(
                "HandleRecordTaskAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)
            .Should()
            .BeNull();

        typeof(ResponsesAgentToolStateGAgent)
            .GetMethod(
                "HandleRecordTaskAsync",
                BindingFlags.Public | BindingFlags.Instance)
            .Should()
            .BeNull();
    }
}
