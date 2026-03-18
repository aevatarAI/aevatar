using System.Reflection;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public sealed class GrpcToolsScriptProtoCompilerTests
{
    [Fact]
    public void ResolveWellKnownProtoRoot_ShouldUseAvailableDescriptorProtoRoot()
    {
        var method = typeof(GrpcToolsScriptProtoCompiler).GetMethod(
            "ResolveWellKnownProtoRoot",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var path = method!.Invoke(null, null).Should().BeOfType<string>().Subject;
        var homebrewInclude = "/opt/homebrew/include";
        var homebrewDescriptor = Path.Combine(homebrewInclude, "google", "protobuf", "descriptor.proto");

        if (File.Exists(homebrewDescriptor))
            path.Should().Be(homebrewInclude);
        else
            path.Should().EndWith("build/native/include");

        File.Exists(Path.Combine(path, "google", "protobuf", "descriptor.proto")).Should().BeTrue();
    }
}
