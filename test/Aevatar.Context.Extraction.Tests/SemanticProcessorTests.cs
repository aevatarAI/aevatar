using Aevatar.Context.Abstractions;
using Aevatar.Context.Core;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.Context.Extraction.Tests;

public sealed class SemanticProcessorTests : IDisposable
{
    private readonly InMemoryContextStore _store = new();
    private readonly IContextLayerGenerator _generator = Substitute.For<IContextLayerGenerator>();
    private readonly SemanticProcessor _sut;

    public SemanticProcessorTests()
    {
        _sut = new SemanticProcessor(_store, _generator);
    }

    public void Dispose() => _sut.Dispose();

    // ─── ProcessTreeAsync ───

    [Fact]
    public async Task ProcessTreeAsync_EmptyDirectory_DoesNotCallGenerator()
    {
        var root = AevatarUri.Parse("aevatar://skills/");
        await _store.CreateDirectoryAsync(root);

        await _sut.ProcessTreeAsync(root);

        await _generator.DidNotReceive().GenerateAbstractAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _generator.DidNotReceive().GenerateDirectoryLayersAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTreeAsync_SingleFile_GeneratesAbstract()
    {
        var dir = AevatarUri.Parse("aevatar://skills/web-search/");
        var file = dir.Join("SKILL.md");
        await _store.WriteAsync(file, "# Web Search\nSearches the web.");

        _generator
            .GenerateAbstractAsync(Arg.Any<string>(), "SKILL.md", Arg.Any<CancellationToken>())
            .Returns("Web search skill.");

        _generator
            .GenerateDirectoryLayersAsync("web-search", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(("Skills collection", "## Overview\nContains web-search."));

        await _sut.ProcessTreeAsync(dir);

        await _generator.Received(1).GenerateAbstractAsync(
            Arg.Any<string>(), "SKILL.md", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTreeAsync_WritesAbstractToStore()
    {
        var dir = AevatarUri.Parse("aevatar://skills/test/");
        var file = dir.Join("doc.md");
        await _store.WriteAsync(file, "Content here.");

        _generator
            .GenerateAbstractAsync("Content here.", "doc.md", Arg.Any<CancellationToken>())
            .Returns("A test document.");

        _generator
            .GenerateDirectoryLayersAsync("test", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(("Test dir abstract", "Test dir overview"));

        await _sut.ProcessTreeAsync(dir);

        var dirAbstract = await _store.GetAbstractAsync(dir);
        dirAbstract.Should().Be("Test dir abstract");

        var dirOverview = await _store.GetOverviewAsync(dir);
        dirOverview.Should().Be("Test dir overview");
    }

    [Fact]
    public async Task ProcessTreeAsync_NestedDirectories_ProcessesBottomUp()
    {
        var root = AevatarUri.Parse("aevatar://resources/project/");
        var subDir = root.Join("docs/");
        var file = subDir.Join("readme.md");
        await _store.WriteAsync(file, "Project readme.");

        var callOrder = new List<string>();

        _generator
            .GenerateAbstractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add($"abstract:{callInfo.ArgAt<string>(1)}");
                return "File abstract.";
            });

        _generator
            .GenerateDirectoryLayersAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add($"dir:{callInfo.ArgAt<string>(0)}");
                return ("Dir abstract", "Dir overview");
            });

        await _sut.ProcessTreeAsync(root);

        callOrder.Should().ContainInOrder("abstract:readme.md", "dir:docs");
    }

    [Fact]
    public async Task ProcessTreeAsync_SkipsFileWithExistingAbstract()
    {
        var dir = AevatarUri.Parse("aevatar://skills/cached/");
        var file = dir.Join("SKILL.md");
        await _store.WriteAsync(file, "Content.");
        await _store.WriteAsync(dir.Join(".abstract.md"), "Already generated.");

        _generator
            .GenerateDirectoryLayersAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(("Dir abstract", "Dir overview"));

        await _sut.ProcessTreeAsync(dir);

        await _generator.DidNotReceive().GenerateAbstractAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Queue / Start ───

    [Fact]
    public async Task EnqueueAndStart_ProcessesQueuedItem()
    {
        var dir = AevatarUri.Parse("aevatar://skills/queued/");
        var file = dir.Join("test.md");
        await _store.WriteAsync(file, "Queued content.");

        _generator
            .GenerateAbstractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Queued abstract.");

        _generator
            .GenerateDirectoryLayersAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(("Dir abstract", "Dir overview"));

        _sut.Start();
        await _sut.EnqueueAsync(dir);

        await Task.Delay(500);

        await _generator.Received().GenerateAbstractAsync(
            Arg.Any<string>(), "test.md", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Start_CalledTwice_DoesNotStartSecondLoop()
    {
        _sut.Start();
        var act = () => _sut.Start();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_StopsProcessing()
    {
        _sut.Start();
        _sut.Dispose();

        var act = () => _sut.EnqueueAsync(AevatarUri.Parse("aevatar://skills/"));
        act.Should().NotThrow();
    }
}
