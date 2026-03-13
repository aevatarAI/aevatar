using Aevatar.Workflow.Application.Abstractions.Authoring;
using Aevatar.Workflow.Application.Authoring;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowAuthoringCommandApplicationServiceTests
{
    [Fact]
    public async Task SaveWorkflowAsync_WhenDraftIsInvalid_ShouldThrowValidationException()
    {
        var service = new WorkflowAuthoringCommandApplicationService(
            new FakeWorkflowDefinitionValidationPort
            {
                ParseResult = new PlaygroundWorkflowParseResult
                {
                    Valid = false,
                    Error = "invalid",
                    Errors = ["invalid yaml"],
                },
            },
            new FakeWorkflowAuthoringPersistencePort());

        var act = async () => await service.SaveWorkflowAsync(new PlaygroundWorkflowSaveRequest
        {
            Yaml = "broken",
        });

        var exception = await act.Should().ThrowAsync<WorkflowAuthoringValidationException>();
        exception.Which.Errors.Should().Equal("invalid yaml");
    }

    [Fact]
    public async Task SaveWorkflowAsync_WhenDraftIsValid_ShouldPassWorkflowNameToPersistencePort()
    {
        var persistencePort = new FakeWorkflowAuthoringPersistencePort
        {
            Result = new PlaygroundWorkflowSaveResult
            {
                Saved = true,
                WorkflowName = "saved_flow",
            },
        };
        var service = new WorkflowAuthoringCommandApplicationService(
            new FakeWorkflowDefinitionValidationPort
            {
                ParseResult = new PlaygroundWorkflowParseResult
                {
                    Valid = true,
                    Definition = new WorkflowAuthoringDefinition
                    {
                        Name = "saved_flow",
                    },
                },
            },
            persistencePort);

        var result = await service.SaveWorkflowAsync(new PlaygroundWorkflowSaveRequest
        {
            Yaml = "name: saved_flow",
            Filename = "saved_flow.yaml",
            Overwrite = true,
        });

        result.Should().BeSameAs(persistencePort.Result);
        persistencePort.LastWorkflowName.Should().Be("saved_flow");
        persistencePort.LastRequest!.Filename.Should().Be("saved_flow.yaml");
    }

    private sealed class FakeWorkflowDefinitionValidationPort : IWorkflowDefinitionValidationPort
    {
        public PlaygroundWorkflowParseResult ParseResult { get; set; } = new();

        public Task<PlaygroundWorkflowParseResult> ParseWorkflowAsync(
            PlaygroundWorkflowParseRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(ParseResult);
    }

    private sealed class FakeWorkflowAuthoringPersistencePort : IWorkflowAuthoringPersistencePort
    {
        public PlaygroundWorkflowSaveRequest? LastRequest { get; private set; }

        public string? LastWorkflowName { get; private set; }

        public PlaygroundWorkflowSaveResult Result { get; set; } = new();

        public Task<PlaygroundWorkflowSaveResult> SaveWorkflowAsync(
            PlaygroundWorkflowSaveRequest request,
            string workflowName,
            CancellationToken ct = default)
        {
            LastRequest = request;
            LastWorkflowName = workflowName;
            return Task.FromResult(Result);
        }
    }
}
