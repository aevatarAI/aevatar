using Aevatar.Context.Abstractions;
using Aevatar.Context.Retrieval;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace Aevatar.Context.Memory.Tests;

public sealed class MemoryExtractionProjectorTests
{
    private readonly IMemoryExtractor _extractor = Substitute.For<IMemoryExtractor>();
    private readonly MemoryDeduplicator _deduplicator;
    private readonly MemoryWriter _writer;
    private readonly IContextVectorIndex _vectorIndex = Substitute.For<IContextVectorIndex>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly IContextStore _store = Substitute.For<IContextStore>();
    private readonly MemoryExtractionProjector<object, object> _sut;

    public MemoryExtractionProjectorTests()
    {
        _deduplicator = new MemoryDeduplicator(_vectorIndex, _embedder);
        _writer = new MemoryWriter(_store);
        _sut = new MemoryExtractionProjector<object, object>(_extractor, _deduplicator, _writer);
    }

    [Fact]
    public void Order_Is200()
    {
        _sut.Order.Should().Be(200);
    }

    [Fact]
    public async Task InitializeAsync_DoesNotThrow()
    {
        var act = async () => await _sut.InitializeAsync(new object());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CompleteAsync_NoMessages_DoesNotCallExtractor()
    {
        var context = new object();
        await _sut.InitializeAsync(context);
        await _sut.CompleteAsync(context, new object());

        await _extractor.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteAsync_WithMessages_CallsExtractor()
    {
        _extractor
            .ExtractAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CandidateMemory>());

        var context = new object();
        await _sut.InitializeAsync(context);

        var envelope = CreateTextEnvelope("Hello from user");
        await _sut.ProjectAsync(context, envelope);

        await _sut.CompleteAsync(context, new object());

        await _extractor.Received(1).ExtractAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteAsync_NoCandidates_DoesNotCallDeduplicator()
    {
        _extractor
            .ExtractAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CandidateMemory>());

        var context = new object();
        await _sut.InitializeAsync(context);

        var envelope = CreateTextEnvelope("Some text");
        await _sut.ProjectAsync(context, envelope);

        await _sut.CompleteAsync(context, new object());

        await _vectorIndex.DidNotReceive().SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<int>(), Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProjectAsync_NullPayload_DoesNotAccumulate()
    {
        var context = new object();
        await _sut.InitializeAsync(context);

        var envelope = new Aevatar.Foundation.Abstractions.EventEnvelope
        {
            Payload = null,
        };
        await _sut.ProjectAsync(context, envelope);

        _extractor
            .ExtractAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CandidateMemory>());

        await _sut.CompleteAsync(context, new object());

        await _extractor.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteAsync_ExtractorThrows_DoesNotPropagate()
    {
        _extractor
            .ExtractAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CandidateMemory>>(_ => throw new InvalidOperationException("LLM error"));

        var context = new object();
        await _sut.InitializeAsync(context);

        var envelope = CreateTextEnvelope("text");
        await _sut.ProjectAsync(context, envelope);

        var act = async () => await _sut.CompleteAsync(context, new object());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InitializeAsync_ResetsMessages()
    {
        var context = new object();

        await _sut.InitializeAsync(context);
        var envelope = CreateTextEnvelope("First run message");
        await _sut.ProjectAsync(context, envelope);

        await _sut.InitializeAsync(context);

        _extractor
            .ExtractAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CandidateMemory>());

        await _sut.CompleteAsync(context, new object());

        await _extractor.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    private static Aevatar.Foundation.Abstractions.EventEnvelope CreateTextEnvelope(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return new Aevatar.Foundation.Abstractions.EventEnvelope
        {
            Payload = new Any
            {
                TypeUrl = "type.googleapis.com/test.TextEvent",
                Value = ByteString.CopyFrom(bytes),
            },
        };
    }
}
