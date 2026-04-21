using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed partial class WorkspaceServiceDeleteDraftTests
{
    [Fact]
    public async Task DeleteDraftAsync_WhenWorkflowIdDoesNotExist_ShouldThrowWorkflowDraftNotFoundException()
    {
        using var environment = new WorkspaceEnvironment();
        const string workflowId = "not-base64!";

        var act = () => environment.Service.DeleteDraftAsync(workflowId);

        await act.Should().ThrowAsync<WorkflowDraftNotFoundException>()
            .Where(exception => exception.WorkflowId == workflowId);
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenGeneratedWorkflowIdDoesNotExist_ShouldThrowWorkflowDraftNotFoundException()
    {
        using var environment = new WorkspaceEnvironment();
        var workflowId = WorkspaceService.CreateStableId("   ");

        var act = () => environment.Service.DeleteDraftAsync(workflowId);

        await act.Should().ThrowAsync<WorkflowDraftNotFoundException>()
            .Where(exception => exception.WorkflowId == workflowId);
    }
}
