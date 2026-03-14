using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public sealed class ScriptBehaviorCompilationRequestTests
{
    [Fact]
    public void Ctor_ShouldNormalizeSingleSourceRequest()
    {
        var request = new ScriptBehaviorCompilationRequest(
            ScriptId: null!,
            Revision: null!,
            Source: "public sealed class Behavior { }");

        request.ScriptId.Should().BeEmpty();
        request.Revision.Should().BeEmpty();
        request.HasProtoFiles.Should().BeFalse();
        request.SourceText.Should().Be("public sealed class Behavior { }");
        request.ResolvedPackageHash.Should().NotBeEmpty();
    }

    [Fact]
    public void Ctor_ShouldSerializePackage_WhenPackageContainsMultipleSourcesOrProtoFiles()
    {
        var package = new ScriptPackageSpec
        {
            EntryBehaviorTypeName = "Behavior",
            EntrySourcePath = "src/Behavior.cs",
            CsharpSources =
            {
                new ScriptPackageFile { Path = "src/Behavior.cs", Content = "public sealed class Behavior { }" },
                new ScriptPackageFile { Path = "src/Support.cs", Content = "public static class Support { }" },
            },
            ProtoFiles =
            {
                new ScriptPackageFile { Path = "proto/messages.proto", Content = "syntax = \"proto3\";" },
            },
        };

        var request = new ScriptBehaviorCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-1",
            Package: package,
            SourceHash: "provided-hash");

        request.HasProtoFiles.Should().BeTrue();
        request.SourceText.Should().Contain("\"cSharpSources\"");
        request.SourceText.Should().Contain("\"protoFiles\"");
        request.ResolvedPackageHash.Should().Be("provided-hash");
    }

    [Fact]
    public void Ctor_ShouldRejectNullSourcePackage()
    {
        var act = () => new ScriptBehaviorCompilationRequest(
            ScriptId: "script-1",
            Revision: "rev-1",
            Package: (ScriptSourcePackage)null!,
            SourceHash: string.Empty);

        act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("Package");
    }

    [Fact]
    public void FromPersistedSource_ShouldDeserializePackage_WhenPayloadIsSerializedPackage()
    {
        var package = new ScriptSourcePackage(
            ScriptSourcePackage.CurrentFormat,
            [new ScriptSourceFile("Behavior.cs", "public sealed class Behavior { }")],
            [new ScriptSourceFile("proto/messages.proto", "syntax = \"proto3\";")],
            "Behavior");
        var persisted = ScriptSourcePackageSerializer.Serialize(package);

        var request = ScriptBehaviorCompilationRequest.FromPersistedSource(
            scriptId: "script-1",
            revision: "rev-1",
            sourceText: persisted,
            sourceHash: string.Empty);

        request.HasProtoFiles.Should().BeTrue();
        request.Package.ProtoFiles.Should().ContainSingle(x => x.Path == "proto/messages.proto");
        request.ResolvedPackageHash.Should().NotBeEmpty();
    }
}
