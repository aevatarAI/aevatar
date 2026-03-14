using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public sealed class ScriptPackageModelTests
{
    [Fact]
    public void ToPackageSpec_ShouldNormalizeSourcesAndEntryPath()
    {
        var package = new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [
                new ScriptSourceFile("./z/Second.cs", "second"),
                new ScriptSourceFile("Behavior.cs", "first"),
            ],
            [
                new ScriptSourceFile("./proto/z.proto", "z"),
                new ScriptSourceFile("proto/a.proto", "a"),
            ],
            "EntryBehavior");

        var spec = ScriptPackageModel.ToPackageSpec(package);

        spec.EntryBehaviorTypeName.Should().Be("EntryBehavior");
        spec.EntrySourcePath.Should().Be("./z/Second.cs");
        spec.CsharpSources.Select(x => x.Path).Should().Equal("./z/Second.cs", "Behavior.cs");
        spec.ProtoFiles.Select(x => x.Path).Should().Equal("./proto/z.proto", "proto/a.proto");
    }

    [Fact]
    public void ToSourcePackage_ShouldReturnEmpty_WhenSpecIsNull()
    {
        var package = ScriptPackageModel.ToSourcePackage(null);

        package.Should().Be(ScriptSourcePackage.Empty);
    }

    [Fact]
    public void ToSourcePackage_ShouldNormalizeAndSortFiles()
    {
        var spec = new ScriptPackageSpec
        {
            EntryBehaviorTypeName = "EntryBehavior",
            EntrySourcePath = "./src/Behavior.cs",
            CsharpSources =
            {
                new ScriptPackageFile { Path = "./src/Behavior.cs", Content = "behavior" },
                new ScriptPackageFile { Path = "src/Other.cs", Content = "other" },
                new ScriptPackageFile { Path = " ", Content = "ignored" },
            },
            ProtoFiles =
            {
                new ScriptPackageFile { Path = "./proto/z.proto", Content = "z" },
                new ScriptPackageFile { Path = "proto/a.proto", Content = "a" },
            },
        };

        var package = ScriptPackageModel.ToSourcePackage(spec);

        package.EntryBehaviorTypeName.Should().Be("EntryBehavior");
        package.CSharpSources.Select(x => x.Path).Should().Equal("./src/Behavior.cs", "file", "src/Other.cs");
        package.ProtoFiles.Select(x => x.Path).Should().Equal("./proto/z.proto", "proto/a.proto");
    }

    [Fact]
    public void CreateSingleSourcePackage_ShouldUseDefaultEntryPathAndContent()
    {
        var package = ScriptPackageModel.CreateSingleSourcePackage("source", entryBehaviorTypeName: "EntryBehavior");

        package.EntryBehaviorTypeName.Should().Be("EntryBehavior");
        package.EntrySourcePath.Should().Be("Behavior.cs");
        package.CsharpSources.Should().ContainSingle();
        package.CsharpSources[0].Path.Should().Be("Behavior.cs");
        package.CsharpSources[0].Content.Should().Be("source");
    }

    [Fact]
    public void GetEntrySourceText_ShouldPreferEntrySourcePath_WhenItMatches()
    {
        var package = new ScriptPackageSpec
        {
            EntrySourcePath = "src/Entry.cs",
            CsharpSources =
            {
                new ScriptPackageFile { Path = "src/Other.cs", Content = "other" },
                new ScriptPackageFile { Path = "src/Entry.cs", Content = "entry" },
            },
        };

        ScriptPackageModel.GetEntrySourceText(package).Should().Be("entry");
        package.GetPrimaryCSharpSource().Should().Be("entry");
    }

    [Fact]
    public void GetEntrySourceText_ShouldFallbackToFirstSource_WhenEntryPathDoesNotMatch()
    {
        var package = new ScriptPackageSpec
        {
            EntrySourcePath = "missing.cs",
            CsharpSources =
            {
                new ScriptPackageFile { Path = "src/First.cs", Content = "first" },
                new ScriptPackageFile { Path = "src/Second.cs", Content = "second" },
            },
        };

        ScriptPackageModel.GetEntrySourceText(package).Should().Be("first");
        package.GetPrimaryCSharpSource().Should().Be("first");
    }

    [Fact]
    public void ComputePackageHash_ShouldBeStableAcrossEquivalentOrdering()
    {
        var left = new ScriptPackageSpec
        {
            EntryBehaviorTypeName = "EntryBehavior",
            EntrySourcePath = "src/Behavior.cs",
            CsharpSources =
            {
                new ScriptPackageFile { Path = "src/Other.cs", Content = "other" },
                new ScriptPackageFile { Path = "./src/Behavior.cs", Content = "behavior" },
            },
            ProtoFiles =
            {
                new ScriptPackageFile { Path = "./proto/z.proto", Content = "z" },
                new ScriptPackageFile { Path = "proto/a.proto", Content = "a" },
            },
        };
        var right = new ScriptPackageSpec
        {
            EntryBehaviorTypeName = "EntryBehavior",
            EntrySourcePath = "./src/Behavior.cs",
            CsharpSources =
            {
                new ScriptPackageFile { Path = "src/Behavior.cs", Content = "behavior" },
                new ScriptPackageFile { Path = "src/Other.cs", Content = "other" },
            },
            ProtoFiles =
            {
                new ScriptPackageFile { Path = "proto/a.proto", Content = "a" },
                new ScriptPackageFile { Path = "proto/z.proto", Content = "z" },
            },
        };

        var leftHash = ScriptPackageModel.ComputePackageHash(left);
        var rightHash = ScriptPackageModel.ComputePackageHash(right);

        leftHash.Should().NotBe(rightHash);
        leftHash.Should().NotBeEmpty();
        rightHash.Should().NotBeEmpty();
    }

    [Fact]
    public void ComputePackageHash_ShouldReturnEmpty_WhenPackageIsNull()
    {
        ScriptPackageModel.ComputePackageHash((ScriptPackageSpec?)null).Should()
            .Be(ScriptPackageModel.ComputePackageHash(ScriptSourcePackage.Empty));
        ScriptPackageModel.ComputePackageHash((ScriptSourcePackage?)null).Should().BeEmpty();
    }

    [Fact]
    public void GetNormalizedFiles_ShouldFilterBlankPaths_AndSort()
    {
        var package = new ScriptPackageSpec
        {
            CsharpSources =
            {
                new ScriptPackageFile { Path = " ./src/B.cs ", Content = "b" },
                new ScriptPackageFile { Path = "src/A.cs", Content = "a" },
                new ScriptPackageFile { Path = "", Content = "ignored" },
            },
            ProtoFiles =
            {
                new ScriptPackageFile { Path = "./proto/B.proto", Content = "b" },
                new ScriptPackageFile { Path = "proto/A.proto", Content = "a" },
            },
        };

        ScriptPackageModel.GetNormalizedCSharpSources(package).Select(x => x.Path)
            .Should().Equal("src/A.cs", "src/B.cs");
        ScriptPackageModel.GetNormalizedProtoFiles(package).Select(x => x.Path)
            .Should().Equal("proto/A.proto", "proto/B.proto");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" ./src/Behavior.cs ", "src/Behavior.cs")]
    [InlineData(".\\src\\Behavior.cs", "src/Behavior.cs")]
    public void NormalizeRelativePath_ShouldNormalizeExpectedValues(string? path, string expected)
    {
        ScriptPackageModel.NormalizeRelativePath(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("/abs/Behavior.cs")]
    [InlineData("../Behavior.cs")]
    [InlineData("src/../../Behavior.cs")]
    [InlineData("..")]
    public void NormalizeRelativePath_ShouldRejectInvalidPaths(string path)
    {
        var act = () => ScriptPackageModel.NormalizeRelativePath(path);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ScriptPackageSpecExtensions_ShouldCoverNullAndProtoHelpers()
    {
        var created = ScriptPackageSpecExtensions.CreateSingleSource("source", path: "", entryBehaviorTypeName: null);
        created.EntrySourcePath.Should().BeEmpty();
        created.CsharpSources.Should().ContainSingle();
        created.CsharpSources[0].Path.Should().Be("Behavior.cs");
        ((ScriptPackageSpec?)null).GetPrimaryCSharpSource().Should().BeEmpty();
        ((ScriptPackageSpec?)null).EnumerateCSharpSources().Should().BeEmpty();
        ((ScriptPackageSpec?)null).EnumerateProtoSources().Should().BeEmpty();
        ((ScriptPackageSpec?)null).HasProtoSources().Should().BeFalse();
        ((ScriptPackageSpec?)null).ClonePackage().Should().NotBeNull();

        var package = new ScriptPackageSpec
        {
            ProtoFiles = { new ScriptPackageFile { Path = "proto/a.proto", Content = "a" } },
        };

        package.HasProtoSources().Should().BeTrue();
        var clone = package.ClonePackage();
        clone.Should().NotBeSameAs(package);
        clone.ProtoFiles.Should().ContainSingle(x => x.Path == "proto/a.proto");
    }
}
