using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

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
        await foreach (var chunk in runtime.ChatStreamAsync("generate an image"))
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
        await foreach (var chunk in runtime.ChatStreamAsync("hello"))
            chunks.Add(chunk);

        chunks.Should().NotContain(c => c.DeltaContentPart != null);
        chunks.Should().Contain(c => c.DeltaContent == "Hello world");
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
            requestBuilder: () => new LLMRequest
            {
                Messages = history.BuildMessages("You are a helpful assistant."),
                Tools = null,
            },
            streamBufferCapacity: 64);
    }

    private sealed class StreamingProvider : ILLMProvider
    {
        private readonly IReadOnlyList<LLMStreamChunk> _chunks;
        public StreamingProvider(IReadOnlyList<LLMStreamChunk> chunks) => _chunks = chunks;
        public string Name => "streaming-test";

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LLMResponse { Content = "response" });

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
}
