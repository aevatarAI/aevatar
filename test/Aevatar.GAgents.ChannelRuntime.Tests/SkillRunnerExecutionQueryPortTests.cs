using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerExecutionQueryPortTests
{
    [Fact]
    public async Task GetAsync_TrimsAgentIdBeforeReading()
    {
        var reader = Substitute.For<IProjectionDocumentReader<SkillRunnerExecutionDocument, string>>();
        reader.GetAsync("runner-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SkillRunnerExecutionDocument?>(new SkillRunnerExecutionDocument
            {
                Id = "runner-1",
                StateVersion = 3,
            }));

        var port = new SkillRunnerExecutionQueryPort(reader);

        var result = await port.GetAsync("  runner-1  ");

        result.Should().NotBeNull();
        result!.Id.Should().Be("runner-1");
        await reader.Received(1).GetAsync("runner-1", Arg.Any<CancellationToken>());
        await reader.DidNotReceive().GetAsync("  runner-1  ", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task GetAsync_BlankAgentId_ReturnsNullWithoutReading(string agentId)
    {
        var reader = Substitute.For<IProjectionDocumentReader<SkillRunnerExecutionDocument, string>>();
        var port = new SkillRunnerExecutionQueryPort(reader);

        var result = await port.GetAsync(agentId);

        result.Should().BeNull();
        await reader.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }

    [Fact]
    public async Task QueryByAgentIdsAsync_BlankAndDuplicateIds_UsesInFilterAndKeepsHighestStateVersion()
    {
        ProjectionDocumentQuery? capturedQuery = null;
        var reader = Substitute.For<IProjectionDocumentReader<SkillRunnerExecutionDocument, string>>();
        reader.QueryAsync(
                Arg.Do<ProjectionDocumentQuery>(query => capturedQuery = query),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<SkillRunnerExecutionDocument>
            {
                Items =
                [
                    new SkillRunnerExecutionDocument
                    {
                        Id = "runner-1",
                        StateVersion = 2,
                        Status = "older",
                    },
                    new SkillRunnerExecutionDocument
                    {
                        Id = "runner-2",
                        StateVersion = 4,
                        Status = "current",
                    },
                    new SkillRunnerExecutionDocument
                    {
                        Id = "runner-1",
                        StateVersion = 7,
                        Status = "latest",
                    },
                    new SkillRunnerExecutionDocument
                    {
                        Id = "",
                        StateVersion = 99,
                    },
                    new SkillRunnerExecutionDocument
                    {
                        Id = "   ",
                        StateVersion = 100,
                    },
                ],
            }));

        var port = new SkillRunnerExecutionQueryPort(reader);

        var result = await port.QueryByAgentIdsAsync(
            [" runner-1 ", "", "runner-2", "runner-1", "   ", "\trunner-2\t"]);

        result.Keys.Should().BeEquivalentTo(["runner-1", "runner-2"]);
        result["runner-1"].StateVersion.Should().Be(7);
        result["runner-1"].Status.Should().Be("latest");
        result["runner-2"].StateVersion.Should().Be(4);

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Take.Should().Be(2);
        capturedQuery.Filters.Should().ContainSingle();
        var filter = capturedQuery.Filters[0];
        filter.FieldPath.Should().Be(nameof(SkillRunnerExecutionDocument.Id));
        filter.Operator.Should().Be(ProjectionDocumentFilterOperator.In);
        filter.Value.Kind.Should().Be(ProjectionDocumentValueKind.StringList);
        filter.Value.RawValue.Should().BeEquivalentTo(new[] { "runner-1", "runner-2" });
    }

    [Fact]
    public async Task QueryByAgentIdsAsync_SingleUniqueId_UsesEqFilter()
    {
        ProjectionDocumentQuery? capturedQuery = null;
        var reader = Substitute.For<IProjectionDocumentReader<SkillRunnerExecutionDocument, string>>();
        reader.QueryAsync(
                Arg.Do<ProjectionDocumentQuery>(query => capturedQuery = query),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectionDocumentQueryResult<SkillRunnerExecutionDocument>
            {
                Items =
                [
                    new SkillRunnerExecutionDocument
                    {
                        Id = "runner-1",
                        StateVersion = 5,
                    },
                ],
            }));

        var port = new SkillRunnerExecutionQueryPort(reader);

        var result = await port.QueryByAgentIdsAsync([" runner-1 ", "runner-1"]);

        result.Should().ContainSingle();
        result["runner-1"].StateVersion.Should().Be(5);

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Take.Should().Be(1);
        capturedQuery.Filters.Should().ContainSingle();
        var filter = capturedQuery.Filters[0];
        filter.FieldPath.Should().Be(nameof(SkillRunnerExecutionDocument.Id));
        filter.Operator.Should().Be(ProjectionDocumentFilterOperator.Eq);
        filter.Value.Kind.Should().Be(ProjectionDocumentValueKind.String);
        filter.Value.RawValue.Should().Be("runner-1");
    }

    [Fact]
    public async Task QueryByAgentIdsAsync_NoUsableIds_ReturnsEmptyWithoutQuerying()
    {
        var reader = Substitute.For<IProjectionDocumentReader<SkillRunnerExecutionDocument, string>>();
        var port = new SkillRunnerExecutionQueryPort(reader);

        var result = await port.QueryByAgentIdsAsync(["", "   ", "\t"]);

        result.Should().BeEmpty();
        await reader.DidNotReceiveWithAnyArgs().QueryAsync(default!, default);
    }
}
