using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Core.Middleware;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;
using ChatFileArtifactRef = Aevatar.AI.Abstractions.LLMProviders.ChatFileRef;
using LlmChatFileSourceKind = Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowFileRefLlmCallMiddlewareTests
{
    private const int MaxLlmMediaBytes = 5 * 1024 * 1024;

    [Fact]
    public async Task AddWorkflowInfrastructure_ShouldMaterializeOpaqueWorkflowMediaBeforeProviderInvocation()
    {
        var imageBytes = "synthetic-image-bytes"u8.ToArray();
        var artifactRef = new ApplicationFileArtifactRef
        {
            FileId = "file-media-alpha",
            ArtifactId = "artifact-media-alpha",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "fixture.png",
            MediaType = "image/png",
            SizeBytes = imageBytes.Length,
            OwnerRunId = "run-media-alpha",
            OwnerScopeId = "scope-media-alpha",
        };
        var readPort = new StaticFileArtifactReadPort(artifactRef, imageBytes);
        var services = new ServiceCollection();
        services.AddSingleton<IFileArtifactReadPort>(readPort);
        services.AddWorkflowInfrastructure();

        await using var provider = services.BuildServiceProvider();
        var context = new LLMCallContext
        {
            Request = BuildRequest(ToContentPart(artifactRef)),
            Provider = new UnusedLlmProvider(),
            IsStreaming = true,
        };
        ContentPart? providerPart = null;

        await MiddlewarePipeline.RunLLMCallAsync(
            provider.GetServices<ILLMCallMiddleware>().ToArray(),
            context,
            () =>
            {
                providerPart = context.Request.Messages.Single().ContentParts!.Single();
                return Task.CompletedTask;
            });

        providerPart.Should().NotBeNull();
        providerPart!.Kind.Should().Be(ContentPartKind.Image);
        providerPart.MediaType.Should().Be("image/png");
        providerPart.Name.Should().Be("fixture.png");
        providerPart.Uri.Should().BeNull();
        providerPart.DataBase64.Should().Be(Convert.ToBase64String(imageBytes));
        providerPart.FileRef.Should().BeEquivalentTo(ToChatFileRef(artifactRef));
        readPort.OpenReadCount.Should().Be(1);
    }

    [Fact]
    public async Task AddWorkflowInfrastructure_ShouldPreserveProviderReadyMediaWithoutReadingArtifacts()
    {
        var artifactRef = new ApplicationFileArtifactRef
        {
            FileId = "file-ready-beta",
            ArtifactId = "artifact-ready-beta",
            SourceKind = ApplicationFileArtifactSourceKind.ExternalResource,
            FileName = "ready.png",
            MediaType = "image/png",
            SizeBytes = 12,
            OwnerRunId = "run-ready-beta",
            OwnerScopeId = "scope-ready-beta",
        };
        var fileRef = ToChatFileRef(artifactRef);
        var parts = new[]
        {
            new ContentPart
            {
                Kind = ContentPartKind.Image,
                Uri = "https://example.invalid/ready.png",
                MediaType = "image/png",
                Name = "https.png",
                FileRef = fileRef,
            },
            new ContentPart
            {
                Kind = ContentPartKind.Image,
                Uri = "http://example.invalid/ready.png",
                MediaType = "image/png",
                Name = "http.png",
                FileRef = fileRef,
            },
            new ContentPart
            {
                Kind = ContentPartKind.Image,
                Uri = "data:image/png;base64,cHJvdmlkZXItcmVhZHk=",
                MediaType = "image/png",
                Name = "data-uri.png",
                FileRef = fileRef,
            },
            new ContentPart
            {
                Kind = ContentPartKind.Image,
                DataBase64 = "aW5saW5lLXJlYWR5",
                MediaType = "image/png",
                Name = "inline.png",
                FileRef = fileRef,
            },
        };
        var readPort = new StaticFileArtifactReadPort(artifactRef, "should-not-be-read"u8.ToArray());
        var services = new ServiceCollection();
        services.AddSingleton<IFileArtifactReadPort>(readPort);
        services.AddWorkflowInfrastructure();

        await using var provider = services.BuildServiceProvider();
        var context = new LLMCallContext
        {
            Request = BuildRequest(parts),
            Provider = new UnusedLlmProvider(),
            IsStreaming = true,
        };

        await MiddlewarePipeline.RunLLMCallAsync(
            provider.GetServices<ILLMCallMiddleware>().ToArray(),
            context,
            () => Task.CompletedTask);

        context.Request.Messages.Single().ContentParts.Should().BeEquivalentTo(parts);
        readPort.OpenReadCount.Should().Be(0);
    }

    [Fact]
    public async Task AddWorkflowInfrastructure_ShouldPreferExistingInlineMediaOverOpaqueUriWithoutReadingArtifact()
    {
        var artifactRef = new ApplicationFileArtifactRef
        {
            FileId = "file-inline-delta",
            ArtifactId = "artifact-inline-delta",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "inline.png",
            MediaType = "image/png",
            SizeBytes = 18,
            OwnerRunId = "run-inline-delta",
            OwnerScopeId = "scope-inline-delta",
        };
        const string inlineBase64 = "aW5saW5lLXByZWZlcnJlZA==";
        var readPort = new StaticFileArtifactReadPort(artifactRef, "should-not-be-read"u8.ToArray());
        var services = new ServiceCollection();
        services.AddSingleton<IFileArtifactReadPort>(readPort);
        services.AddWorkflowInfrastructure();

        await using var provider = services.BuildServiceProvider();
        var context = new LLMCallContext
        {
            Request = BuildRequest(new ContentPart
            {
                Kind = ContentPartKind.Image,
                DataBase64 = inlineBase64,
                Uri = artifactRef.ArtifactId,
                MediaType = artifactRef.MediaType,
                Name = artifactRef.FileName,
                FileRef = ToChatFileRef(artifactRef),
            }),
            Provider = new UnusedLlmProvider(),
            IsStreaming = true,
        };

        await MiddlewarePipeline.RunLLMCallAsync(
            provider.GetServices<ILLMCallMiddleware>().ToArray(),
            context,
            () => Task.CompletedTask);

        var providerPart = context.Request.Messages.Single().ContentParts!.Single();
        providerPart.DataBase64.Should().Be(inlineBase64);
        providerPart.Uri.Should().BeNull();
        providerPart.FileRef.Should().BeEquivalentTo(ToChatFileRef(artifactRef));
        readPort.OpenReadCount.Should().Be(0);
    }

    [Fact]
    public async Task AddWorkflowInfrastructure_ShouldRejectOversizedWorkflowMediaBeforeProviderInvocation()
    {
        var artifactRef = new ApplicationFileArtifactRef
        {
            FileId = "file-oversized-gamma",
            ArtifactId = "artifact-oversized-gamma",
            SourceKind = ApplicationFileArtifactSourceKind.ChatInput,
            FileName = "oversized.png",
            MediaType = "image/png",
            SizeBytes = 1,
            OwnerRunId = "run-oversized-gamma",
            OwnerScopeId = "scope-oversized-gamma",
        };
        var readPort = new StaticFileArtifactReadPort(
            artifactRef,
            new byte[MaxLlmMediaBytes + 1]);
        var services = new ServiceCollection();
        services.AddSingleton<IFileArtifactReadPort>(readPort);
        services.AddWorkflowInfrastructure();

        await using var provider = services.BuildServiceProvider();
        var context = new LLMCallContext
        {
            Request = BuildRequest(ToContentPart(artifactRef)),
            Provider = new UnusedLlmProvider(),
            IsStreaming = true,
        };
        var providerInvoked = false;

        var act = () => MiddlewarePipeline.RunLLMCallAsync(
            provider.GetServices<ILLMCallMiddleware>().ToArray(),
            context,
            () =>
            {
                providerInvoked = true;
                return Task.CompletedTask;
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{MaxLlmMediaBytes} bytes*");
        providerInvoked.Should().BeFalse();
    }

    private static LLMRequest BuildRequest(params ContentPart[] parts) =>
        new()
        {
            Messages =
            [
                ChatMessage.User(parts, "inspect the synthetic image"),
            ],
        };

    private static ContentPart ToContentPart(ApplicationFileArtifactRef fileRef) =>
        new()
        {
            Kind = ContentPartKind.Image,
            Uri = fileRef.ArtifactId,
            MediaType = fileRef.MediaType,
            Name = fileRef.FileName,
            FileRef = ToChatFileRef(fileRef),
        };

    private static ChatFileArtifactRef ToChatFileRef(ApplicationFileArtifactRef fileRef) =>
        new()
        {
            FileId = fileRef.FileId,
            ArtifactId = fileRef.ArtifactId,
            SourceKind = LlmChatFileSourceKind.ChatInput,
            FileName = fileRef.FileName,
            MediaType = fileRef.MediaType,
            SizeBytes = fileRef.SizeBytes,
            OwnerRunId = fileRef.OwnerRunId,
            OwnerScopeId = fileRef.OwnerScopeId,
        };

    private sealed class StaticFileArtifactReadPort(
        ApplicationFileArtifactRef descriptor,
        byte[] content) : IFileArtifactReadPort
    {
        public int OpenReadCount { get; private set; }

        public ValueTask<ApplicationFileArtifactRef> DescribeAsync(
            ApplicationFileArtifactRef fileRef,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(descriptor);

        public ValueTask<FileArtifactContent> OpenReadAsync(
            ApplicationFileArtifactRef fileRef,
            CancellationToken cancellationToken = default)
        {
            OpenReadCount++;
            return ValueTask.FromResult<FileArtifactContent>(
                new(descriptor, new MemoryStream(content, writable: false)));
        }
    }

    private sealed class UnusedLlmProvider : ILLMProvider
    {
        public string Name => "unused";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
