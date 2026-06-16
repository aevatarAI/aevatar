using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.Bootstrap.Extensions.AI.OrnnPublishing;
using Aevatar.Scripting.Core.Compilation;
using FluentAssertions;

namespace Aevatar.Bootstrap.Tests;

public sealed class ScriptOrnnSkillPublishAssetValidatorTests
{
    [Fact]
    public async Task OrnnPublishValidateAsync_ShouldFailClosedWhenCompilerIsMissing()
    {
        var validator = new ScriptOrnnSkillPublishAssetValidator();

        var diagnostics = await validator.ValidateAsync(RequestWithScript("main.cs", "class C {}"));

        diagnostics.Should().ContainSingle()
            .Which.Code.Should().Be("missing_script_compiler");
    }

    [Fact]
    public async Task OrnnPublishValidateAsync_ShouldPassSourcePackageToCompilerAndReturnDiagnosticsOnly()
    {
        var compiler = new CapturingCompiler(["sandbox violation"]);
        var validator = new ScriptOrnnSkillPublishAssetValidator(compiler);

        var diagnostics = await validator.ValidateAsync(RequestWithScript(
            "src/Main.cs",
            "public sealed class Main {}",
            new OrnnSkillPublishScript
            {
                Path = "contracts/service.proto",
                Content = "syntax = \"proto3\";",
            }));

        diagnostics.Should().ContainSingle()
            .Which.Should().Be(new OrnnSkillPublishDiagnostic("invalid_script", "sandbox violation", "scripts"));
        compiler.Requests.Should().ContainSingle();
        var request = compiler.Requests[0];
        request.ScriptId.Should().Be("script-skill");
        request.Revision.Should().Be("1.0");
        request.Package.CSharpSources.Should().ContainSingle()
            .Which.Should().Be(new ScriptSourceFile("src/Main.cs", "public sealed class Main {}"));
        request.Package.ProtoFiles.Should().ContainSingle()
            .Which.Should().Be(new ScriptSourceFile("contracts/service.proto", "syntax = \"proto3\";"));
    }

    private static OrnnSkillPublishRequest RequestWithScript(
        string path,
        string content,
        params OrnnSkillPublishScript[] additionalScripts) => new()
    {
        Name = "script-skill",
        Description = "Script skill",
        Version = "1.0",
        Category = "plain",
        InstructionsMarkdown = "Run script.",
        Scripts =
        [
            new OrnnSkillPublishScript { Path = path, Content = content },
            .. additionalScripts,
        ],
    };

    private sealed class CapturingCompiler(IReadOnlyList<string> diagnostics) : IScriptBehaviorCompiler
    {
        public List<ScriptBehaviorCompilationRequest> Requests { get; } = [];

        public ScriptBehaviorCompilationResult Compile(ScriptBehaviorCompilationRequest request)
        {
            Requests.Add(request);
            return new ScriptBehaviorCompilationResult(diagnostics.Count == 0, null, diagnostics);
        }
    }
}
