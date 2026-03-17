using System.Reflection;
using Aevatar.Scripting.Infrastructure.Compilation;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Compilation;

public sealed class GrpcToolsScriptProtoCompilerTests
{
    [Fact]
    public void ResolveWellKnownProtoRoot_WhenHomebrewIncludeMissesDescriptorProto_ShouldFallbackToGrpcToolsInclude()
    {
        var method = typeof(GrpcToolsScriptProtoCompiler).GetMethod(
            "ResolveWellKnownProtoRoot",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var path = method!.Invoke(null, null).Should().BeOfType<string>().Subject;

        path.Should().EndWith("build/native/include");
        File.Exists(Path.Combine(path, "google", "protobuf", "descriptor.proto")).Should().BeTrue();
    }
}
