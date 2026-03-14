using System.Text.RegularExpressions;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.Architecture;

public sealed class ScriptingReadModelWrapperGuardTests
{
    private static readonly Regex UnsupportedWrapperPattern = new(
        "wrappers\\.proto|google\\.protobuf\\.(StringValue|BoolValue|Int32Value|Int64Value|UInt32Value|UInt64Value|DoubleValue|FloatValue|BytesValue)",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("test/Aevatar.Scripting.Core.Tests/Protos/script_behavior_test_messages.proto")]
    [InlineData("test/Aevatar.Integration.Tests/Protos/claim_protocol.proto")]
    [InlineData("test/Aevatar.Integration.Tests/Protos/hybrid_service_upgrade_protocol.proto")]
    [InlineData("test/Aevatar.Integration.Tests/Protos/script_evolution_protocol.proto")]
    [InlineData("test/Aevatar.Integration.Tests/Protos/text_normalization_protocol.proto")]
    [InlineData("test/Aevatar.Scripting.Core.Tests/ScriptSources.cs")]
    [InlineData("test/Aevatar.Scripting.Core.Tests/ClaimScriptSources.cs")]
    [InlineData("test/Aevatar.Integration.Tests/ScriptEvolutionIntegrationSources.cs")]
    public void ScriptingFixtures_ShouldNotUseUnsupportedWrapperReadModelTypes(string relativePath)
    {
        var repoRoot = FindRepoRoot();
        var sourcePath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(sourcePath).Should().BeTrue("wrapper guard expects fixture file: {0}", sourcePath);

        var source = File.ReadAllText(sourcePath);
        UnsupportedWrapperPattern.IsMatch(source).Should().BeFalse(
            "scripting fixtures should use scalar fields, proto3 optional fields, or typed sub-messages instead of protobuf wrappers: {0}",
            relativePath);
    }

    [Fact]
    public void GuardScript_ShouldTargetCurrentScriptingFixtureSources()
    {
        var repoRoot = FindRepoRoot();
        var guardPath = Path.Combine(repoRoot, "tools", "ci", "scripting_readmodel_wrapper_guard.sh");
        File.Exists(guardPath).Should().BeTrue("guard script must exist at {0}", guardPath);

        var source = File.ReadAllText(guardPath);
        source.Should().Contain("test/Aevatar.Scripting.Core.Tests/Protos");
        source.Should().Contain("test/Aevatar.Integration.Tests/Protos");
        source.Should().Contain("test/Aevatar.Scripting.Core.Tests/ScriptSources.cs");
        source.Should().Contain("test/Aevatar.Scripting.Core.Tests/ClaimScriptSources.cs");
        source.Should().Contain("test/Aevatar.Integration.Tests/ScriptEvolutionIntegrationSources.cs");
        source.Should().Contain("StringValue");
        source.Should().Contain("BoolValue");
        source.Should().Contain("BytesValue");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var marker = Path.Combine(dir.FullName, "aevatar.slnx");
            if (File.Exists(marker))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Cannot locate repository root from test base directory.");
    }
}
