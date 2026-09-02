using System.Security.Cryptography;
using System.Text;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.Studio.Hosting.ContentArtifacts;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests.ContentArtifacts;

public sealed class WorkflowFileContentArtifactBackingContentPortTests
{
    [Fact]
    public async Task Read_ShouldMapStableWorkflowArtifactIdentityAndOwnership()
    {
        var content = Encoding.UTF8.GetBytes("durable report");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var workflowFiles = new RecordingFileArtifactReadPort(new FileArtifactRef
        {
            FileId = "wf-file-1",
            ArtifactId = "workflow-file://wf-file-1",
            OwnerScopeId = "scope-1",
            OwnerRunId = "run-1",
            SizeBytes = content.LongLength,
            Sha256 = hash,
        }, content);
        var port = new WorkflowFileContentArtifactBackingContentPort(workflowFiles);
        var request = new ContentArtifactBackingContentRequest(
            new ContentArtifactBackingObjectReference
            {
                Provider = WorkflowFileContentArtifactBackingContentPort.Provider,
                ObjectKey = "workflow-file://wf-file-1",
            },
            "scope-1",
            "run-1");

        var descriptor = await port.DescribeAsync(request);
        await using var stream = await port.OpenReadAsync(request);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);

        descriptor.Should().Be(new ContentArtifactBackingContentDescriptor(content.LongLength, hash));
        buffer.ToArray().Should().Equal(content);
        workflowFiles.Requests.Should().OnlyContain(fileRef =>
            fileRef.ArtifactId == "workflow-file://wf-file-1" &&
            fileRef.OwnerScopeId == "scope-1" &&
            fileRef.OwnerRunId == "run-1");
    }

    [Fact]
    public async Task Describe_ShouldFailClosedWhenWorkflowOwnershipDoesNotMatch()
    {
        var content = Encoding.UTF8.GetBytes("other scope");
        var workflowFiles = new RecordingFileArtifactReadPort(new FileArtifactRef
        {
            ArtifactId = "workflow-file://wf-file-1",
            OwnerScopeId = "scope-2",
            OwnerRunId = "run-1",
            SizeBytes = content.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
        }, content);
        var port = new WorkflowFileContentArtifactBackingContentPort(workflowFiles);

        var act = () => port.DescribeAsync(new ContentArtifactBackingContentRequest(
            new ContentArtifactBackingObjectReference
            {
                Provider = WorkflowFileContentArtifactBackingContentPort.Provider,
                ObjectKey = "workflow-file://wf-file-1",
            },
            "scope-1",
            "run-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to the ContentArtifact Scope*");
    }

    [Fact]
    public async Task Describe_ShouldFailClosedWhenWorkflowProviderIsUnavailable()
    {
        var port = new WorkflowFileContentArtifactBackingContentPort();

        var act = () => port.DescribeAsync(new ContentArtifactBackingContentRequest(
            new ContentArtifactBackingObjectReference
            {
                Provider = WorkflowFileContentArtifactBackingContentPort.Provider,
                ObjectKey = "workflow-file://wf-file-1",
            },
            "scope-1",
            "run-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*provider is unavailable*");
    }

    [Fact]
    public async Task DependencyInjection_ShouldKeepInlineArtifactsAvailableWithoutWorkflowFilePort()
    {
        var services = new ServiceCollection();
        services.AddSingleton<
            IContentArtifactBackingContentPort,
            WorkflowFileContentArtifactBackingContentPort>();
        await using var provider = services.BuildServiceProvider();

        var port = provider.GetRequiredService<IContentArtifactBackingContentPort>();
        var act = () => port.DescribeAsync(new ContentArtifactBackingContentRequest(
            new ContentArtifactBackingObjectReference
            {
                Provider = WorkflowFileContentArtifactBackingContentPort.Provider,
                ObjectKey = "workflow-file://wf-file-1",
            },
            "scope-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*provider is unavailable*");
    }

    private sealed class RecordingFileArtifactReadPort(
        FileArtifactRef descriptor,
        byte[] content) : IFileArtifactReadPort
    {
        public List<FileArtifactRef> Requests { get; } = [];

        public ValueTask<FileArtifactRef> DescribeAsync(
            FileArtifactRef fileRef,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(fileRef);
            return ValueTask.FromResult(descriptor);
        }

        public ValueTask<FileArtifactContent> OpenReadAsync(
            FileArtifactRef fileRef,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(fileRef);
            return ValueTask.FromResult(new FileArtifactContent(
                descriptor,
                new MemoryStream(content, writable: false)));
        }
    }
}
