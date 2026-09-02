using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using FluentAssertions;
using LlmChatFileRef = Aevatar.AI.Abstractions.LLMProviders.ChatFileRef;
using LlmChatFileSourceKind = Aevatar.AI.Abstractions.LLMProviders.ChatFileSourceKind;

namespace Aevatar.AI.Tests;

public class MultimodalPipelineTests
{
    // ─── ResolveRequestInputParts (tested via RoleGAgent static helper pattern) ───

    [Fact]
    public void ContentPart_TextPart_ShouldCreateTextKind()
    {
        var part = ContentPart.TextPart("hello");
        part.Kind.Should().Be(ContentPartKind.Text);
        part.Text.Should().Be("hello");
    }

    [Fact]
    public void ContentPart_ImagePart_ShouldCreateImageKind()
    {
        var part = ContentPart.ImagePart("base64data", "image/png", "photo.png");
        part.Kind.Should().Be(ContentPartKind.Image);
        part.DataBase64.Should().Be("base64data");
        part.MediaType.Should().Be("image/png");
        part.Name.Should().Be("photo.png");
    }

    [Fact]
    public void ContentPart_AudioPart_ShouldCreateAudioKind()
    {
        var part = ContentPart.AudioPart("audiodata", "audio/wav", "voice.wav");
        part.Kind.Should().Be(ContentPartKind.Audio);
        part.DataBase64.Should().Be("audiodata");
        part.MediaType.Should().Be("audio/wav");
    }

    [Fact]
    public void ContentPart_VideoPart_ShouldCreateVideoKind()
    {
        var part = ContentPart.VideoPart("videodata", "video/mp4", "clip.mp4");
        part.Kind.Should().Be(ContentPartKind.Video);
        part.DataBase64.Should().Be("videodata");
        part.MediaType.Should().Be("video/mp4");
    }

    // ─── ContentPartProtoMapper roundtrip ───

    [Fact]
    public void ContentPartProtoMapper_RoundTrip_ShouldPreserveAllFields()
    {
        var original = ContentPart.ImagePart("Zm9v", "image/jpeg", "test.jpg");

        var proto = ContentPartProtoMapper.ToProto(original);
        proto.Kind.Should().Be(ChatContentPartKind.Image);
        proto.DataBase64.Should().Be("Zm9v");
        proto.MediaType.Should().Be("image/jpeg");
        proto.Name.Should().Be("test.jpg");

        var roundTripped = ContentPartProtoMapper.FromProto(proto);
        roundTripped.Kind.Should().Be(ContentPartKind.Image);
        roundTripped.DataBase64.Should().Be("Zm9v");
        roundTripped.MediaType.Should().Be("image/jpeg");
        roundTripped.Name.Should().Be("test.jpg");
    }

    [Fact]
    public void ContentPartProtoMapper_RoundTrip_ShouldPreserveFileRef()
    {
        var original = ContentPart.ImageFileRefPart(
            new LlmChatFileRef
            {
                FileId = "file-1",
                ArtifactId = "workflow-file://file-1",
                SourceKind = LlmChatFileSourceKind.ChatInput,
                SourceMessageId = "om_1",
                SourceResourceKey = "img_1",
                FileName = "photo.png",
                MediaType = "image/png",
                SizeBytes = 3,
                Sha256 = "sha",
                CreatedAtUnixMs = 1_000,
                ExpiresAtUnixMs = 2_000,
                OwnerRunId = "run-1",
                OwnerScopeId = "scope-1",
            });

        var proto = ContentPartProtoMapper.ToProto(original);
        proto.FileRef.FileId.Should().Be("file-1");
        proto.FileRef.ArtifactId.Should().Be("workflow-file://file-1");
        proto.FileRef.SourceKind.Should().Be(Aevatar.AI.Abstractions.ChatFileSourceKind.ChatInput);
        proto.FileRef.SourceMessageId.Should().Be("om_1");
        proto.FileRef.SourceResourceKey.Should().Be("img_1");
        proto.FileRef.SizeBytes.Should().Be(3);

        var roundTripped = ContentPartProtoMapper.FromProto(proto);
        roundTripped.FileRef.Should().NotBeNull();
        roundTripped.FileRef!.FileId.Should().Be("file-1");
        roundTripped.FileRef.ArtifactId.Should().Be("workflow-file://file-1");
        roundTripped.FileRef.SourceKind.Should().Be(LlmChatFileSourceKind.ChatInput);
        roundTripped.FileRef.SourceMessageId.Should().Be("om_1");
        roundTripped.FileRef.SourceResourceKey.Should().Be("img_1");
        roundTripped.FileRef.FileName.Should().Be("photo.png");
        roundTripped.FileRef.MediaType.Should().Be("image/png");
        roundTripped.FileRef.SizeBytes.Should().Be(3);
        roundTripped.FileRef.Sha256.Should().Be("sha");
        roundTripped.FileRef.CreatedAtUnixMs.Should().Be(1_000);
        roundTripped.FileRef.ExpiresAtUnixMs.Should().Be(2_000);
        roundTripped.FileRef.OwnerRunId.Should().Be("run-1");
        roundTripped.FileRef.OwnerScopeId.Should().Be("scope-1");
    }

    [Fact]
    public void ContentPartProtoMapper_ToProtoList_ShouldHandleMultipleParts()
    {
        var parts = new List<ContentPart>
        {
            ContentPart.TextPart("describe this image"),
            ContentPart.ImagePart("aW1hZ2U=", "image/png"),
        };

        var protos = ContentPartProtoMapper.ToProtoList(parts);
        protos.Should().HaveCount(2);
        protos[0].Kind.Should().Be(ChatContentPartKind.Text);
        protos[0].Text.Should().Be("describe this image");
        protos[1].Kind.Should().Be(ChatContentPartKind.Image);
        protos[1].DataBase64.Should().Be("aW1hZ2U=");
    }

    [Fact]
    public void ContentPartProtoMapper_NullInput_ShouldReturnEmpty()
    {
        var result = ContentPartProtoMapper.FromProtoList(null);
        result.Should().BeEmpty();

        var result2 = ContentPartProtoMapper.ToProtoList(null);
        result2.Should().BeEmpty();
    }

    // ─── ChatMessage.User multimodal overload ───

    [Fact]
    public void ChatMessage_UserWithContentParts_ShouldPreserveBoth()
    {
        var parts = new List<ContentPart>
        {
            ContentPart.TextPart("what is this?"),
            ContentPart.ImagePart("data", "image/png"),
        };

        var msg = ChatMessage.User(parts, "what is this?");
        msg.Role.Should().Be("user");
        msg.Content.Should().Be("what is this?");
        msg.ContentParts.Should().HaveCount(2);
        msg.ContentParts![0].Kind.Should().Be(ContentPartKind.Text);
        msg.ContentParts[1].Kind.Should().Be(ContentPartKind.Image);
    }

    // ─── NormalizeStreamChunk DeltaContentPart forwarding ───

    [Fact]
    public async Task ChatRuntime_StreamAsync_ShouldForwardDeltaContentPart()
    {
        // Provider that returns a media content part in the stream
        var imagePart = ContentPart.ImagePart("Zm9v", "image/png", "generated.png");
        var provider = new StreamingProvider(
        [
            new LLMStreamChunk { DeltaContent = "Here is the image:" },
            new LLMStreamChunk { DeltaContentPart = imagePart },
            new LLMStreamChunk { IsLast = true },
        ]);
        var runtime = CreateRuntime(provider);

        var chunks = new List<LLMStreamChunk>();
        await foreach (var chunk in runtime.ChatStreamAsync("generate an image", turnCatalog: null))
            chunks.Add(chunk);

        // The DeltaContentPart should be forwarded through
        chunks.Should().Contain(c => c.DeltaContentPart != null);
        var mediaChunk = chunks.First(c => c.DeltaContentPart != null);
        mediaChunk.DeltaContentPart!.Kind.Should().Be(ContentPartKind.Image);
        mediaChunk.DeltaContentPart.DataBase64.Should().Be("Zm9v");
    }

    [Fact]
    public async Task ChatRuntime_StreamAsync_TextOnlyMessage_ShouldNotIncludeMediaParts()
    {
        var provider = new StreamingProvider(
        [
            new LLMStreamChunk { DeltaContent = "Hello world" },
            new LLMStreamChunk { IsLast = true },
        ]);
        var runtime = CreateRuntime(provider);

        var chunks = new List<LLMStreamChunk>();
        await foreach (var chunk in runtime.ChatStreamAsync("hello", turnCatalog: null))
            chunks.Add(chunk);

        chunks.Should().NotContain(c => c.DeltaContentPart != null);
        chunks.Should().Contain(c => c.DeltaContent == "Hello world");
    }

    [Fact]
    public async Task ChatRuntime_WhenToolVisibilityRestricted_ShouldOnlyExposeAllowedToolsToProvider()
    {
        var provider = new RecordingProvider();
        var toolManager = new ToolManager();
        toolManager.Register(new NamedTool("search"));
        toolManager.Register(new NamedTool("calendar"));
        var runtime = CreateRuntime(
            provider,
            toolManager,
            AgentToolExecutionContext.Empty with
            {
                ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(["search"]),
            });

        await foreach (var _ in runtime.ChatStreamAsync("hello", turnCatalog: null))
        {
        }

        provider.Requests.Should().ContainSingle();
        provider.Requests[0].Tools.Should().NotBeNull();
        provider.Requests[0].Tools!.Select(static tool => tool.Name).Should().Equal("search");
    }

    [Fact]
    public async Task ChatRuntime_WhenToolVisibilityPresentEmpty_ShouldExposeNoToolsToProvider()
    {
        var provider = new RecordingProvider();
        var toolManager = new ToolManager();
        toolManager.Register(new NamedTool("search"));
        var runtime = CreateRuntime(
            provider,
            toolManager,
            AgentToolExecutionContext.Empty with
            {
                ToolVisibility = AgentToolVisibilityScope.Empty,
            });

        await foreach (var _ in runtime.ChatStreamAsync("hello", turnCatalog: null))
        {
        }

        provider.Requests.Should().ContainSingle();
        provider.Requests[0].Tools.Should().BeNull();
    }

    // ─── Helpers ───

    private static ChatRuntime CreateRuntime(ILLMProvider provider)
    {
        var history = new ChatHistory();
        var toolLoop = new ToolCallLoop(new ToolManager());
        return new ChatRuntime(
            providerFactory: () => provider,
            history: history,
            toolLoop: toolLoop,
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = history.BuildMessages("You are a helpful assistant."),
                Tools = null,
            });
    }

    private static ChatRuntime CreateRuntime(
        ILLMProvider provider,
        ToolManager toolManager,
        AgentToolExecutionContext toolContext)
    {
        var history = new ChatHistory();
        var toolLoop = new ToolCallLoop(toolManager);
        return new ChatRuntime(
            providerFactory: () => provider,
            history: history,
            toolLoop: toolLoop,
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = history.BuildMessages("You are a helpful assistant."),
                Tools = toolManager.GetAll(),
                ToolContext = toolContext,
            });
    }

    private sealed class StreamingProvider : ILLMProvider
    {
        private readonly IReadOnlyList<LLMStreamChunk> _chunks;
        public StreamingProvider(IReadOnlyList<LLMStreamChunk> chunks) => _chunks = chunks;
        public string Name => "streaming-test";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var chunk in _chunks)
            {
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private sealed class RecordingProvider : ILLMProvider
    {
        public string Name => "recording-test";

        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new LLMStreamChunk { DeltaContent = "ok" };
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class NamedTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
