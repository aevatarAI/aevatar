using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowChatRunRequestSeedTests
{
    [Fact]
    public void WorkflowChatRunRequest_ShouldExposeContextSeedsAndHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trace"] = "trace-1",
        };
        var request = new WorkflowChatRunRequest(
            Prompt: "hello",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            Headers: headers,
            CommandIdSeed: "cmd-1",
            CorrelationIdSeed: "corr-1");

        var seed = request.Should().BeAssignableTo<ICommandContextSeed>().Subject;

        seed.CommandId.Should().Be("cmd-1");
        seed.CorrelationId.Should().Be("corr-1");
        seed.Headers.Should().BeSameAs(headers);
    }

    [Fact]
    public void WorkflowChatRunRequest_ShouldNotSerializeTargetSeed()
    {
        var request = new WorkflowChatRunRequest(
            Prompt: "hello",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            TargetSeed: new WorkflowRunTargetSeed(
                ActorId: "run-1",
                WorkflowNameForRun: "direct",
                CreatedActorIds: ["definition-1", "run-1"],
                Source: WorkflowChatSource.CatalogWorkflow("direct")));

        var json = JsonSerializer.Serialize(request);

        json.Should().NotContain("TargetSeed");
        json.Should().NotContain("run-1");
    }
}
