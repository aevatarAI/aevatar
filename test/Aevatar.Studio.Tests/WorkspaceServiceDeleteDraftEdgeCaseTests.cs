using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed partial class WorkspaceServiceDeleteDraftTests
{
    [Fact]
    public async Task DeleteDraftAsync_WhenWorkflowIdIsNotStableId_ShouldThrowInvalidOperationException()
    {
        using var environment = new WorkspaceEnvironment();

        var act = () => environment.Service.DeleteDraftAsync("not-base64!");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("workflowId is invalid.");
    }

    [Fact]
    public async Task DeleteDraftAsync_WhenStableIdDecodesToWhitespace_ShouldThrowInvalidOperationException()
    {
        using var environment = new WorkspaceEnvironment();
        var workflowId = WorkspaceService.CreateStableId("   ");

        var act = () => environment.Service.DeleteDraftAsync(workflowId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("workflowId is invalid.");
    }
}
