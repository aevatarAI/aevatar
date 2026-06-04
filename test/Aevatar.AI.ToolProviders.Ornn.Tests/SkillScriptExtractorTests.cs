using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class SkillScriptExtractorTests
{
    [Fact]
    public void ExtractFromFiles_PrefersScriptsDirectoryOverAssets()
    {
        var files = new Dictionary<string, string>
        {
            ["scripts/Main.cs"] = "public sealed class MainBehavior {}",
            ["scripts/model.proto"] = "syntax = \"proto3\";",
            ["assets/Legacy.cs"] = "public sealed class LegacyBehavior {}",
            ["assets/model.proto"] = "syntax = \"proto3\";",
            ["docs/readme.md"] = "reference",
        };

        var result = new SkillScriptExtractor().ExtractFromFiles("Daily Skill", null, files);

        var script = result.Scripts.Should().ContainSingle().Subject;
        script.ScriptId.Should().Be("daily-skill-main");
        script.SourceFiles.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>(
                "scripts/Main.cs",
                "public sealed class MainBehavior {}"));
        script.ProtoFiles.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>(
                "scripts/model.proto",
                "syntax = \"proto3\";"));
        result.RemainingFiles.Should().ContainKeys(
            "assets/Legacy.cs",
            "assets/model.proto",
            "docs/readme.md");
        result.RemainingFiles.Should().NotContainKey("scripts/Main.cs");
        result.RemainingFiles.Should().NotContainKey("scripts/model.proto");
    }

    [Fact]
    public void ExtractFromFiles_FallsBackToAssetsWhenScriptsDirectoryHasNoSource()
    {
        var files = new Dictionary<string, string>
        {
            ["scripts/README.md"] = "docs",
            ["assets/Run.cs"] = "public sealed class RunBehavior {}",
            ["assets/run.proto"] = "syntax = \"proto3\";",
            ["assets/nested/Ignored.cs"] = "public sealed class NestedBehavior {}",
        };

        var result = new SkillScriptExtractor().ExtractFromFiles("Skill Name", null, files);

        var script = result.Scripts.Should().ContainSingle().Subject;
        script.ScriptId.Should().Be("skill-name-run");
        script.SourceFiles.Should().ContainSingle().Which.Key.Should().Be("assets/Run.cs");
        script.ProtoFiles.Should().ContainSingle().Which.Key.Should().Be("assets/run.proto");
        result.RemainingFiles.Should().ContainKeys("scripts/README.md", "assets/nested/Ignored.cs");
        result.RemainingFiles.Should().NotContainKey("assets/Run.cs");
        result.RemainingFiles.Should().NotContainKey("assets/run.proto");
    }

    [Fact]
    public void ExtractFromFiles_IgnoresBlankCSharpAndDoesNotConsumeItsProto()
    {
        var files = new Dictionary<string, string>
        {
            ["scripts/Blank.cs"] = " \n\t ",
            ["scripts/model.proto"] = "syntax = \"proto3\";",
            ["assets/Fallback.cs"] = "public sealed class FallbackBehavior {}",
            ["assets/fallback.proto"] = "syntax = \"proto3\";",
        };

        var result = new SkillScriptExtractor().ExtractFromFiles("Blank Skill", null, files);

        var script = result.Scripts.Should().ContainSingle().Subject;
        script.SourceFiles.Should().ContainSingle().Which.Key.Should().Be("assets/Fallback.cs");
        script.ProtoFiles.Should().ContainSingle().Which.Key.Should().Be("assets/fallback.proto");
        result.RemainingFiles.Should().ContainKeys("scripts/Blank.cs", "scripts/model.proto");
    }

    [Fact]
    public void ExtractFromFiles_PropagatesScriptEntry()
    {
        var files = new Dictionary<string, string>
        {
            ["scripts/Entry.cs"] = "public sealed class EntryBehavior {}",
        };

        var result = new SkillScriptExtractor().ExtractFromFiles("Entry Skill", "My.Namespace.EntryBehavior", files);

        result.Scripts.Should().ContainSingle()
            .Which.EntryBehaviorTypeName.Should().Be("My.Namespace.EntryBehavior");
    }

    [Fact]
    public void ExtractFromFiles_ReturnsNullRemainingWhenAllFilesAreConsumed()
    {
        var files = new Dictionary<string, string>
        {
            ["scripts/Main.cs"] = "public sealed class MainBehavior {}",
            ["scripts/model.proto"] = "syntax = \"proto3\";",
        };

        var result = new SkillScriptExtractor().ExtractFromFiles("Skill", null, files);

        result.Scripts.Should().ContainSingle();
        result.RemainingFiles.Should().BeNull();
    }

    [Fact]
    public void ExtractFromDirectory_ReadsScriptsDirectoryAndEntryHint()
    {
        using var tempDir = new TempDirectory();
        var scriptsDir = Path.Combine(tempDir.Path, "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(
            Path.Combine(scriptsDir, "Main.cs"),
            "public sealed class MainBehavior {}");
        File.WriteAllText(
            Path.Combine(scriptsDir, "contract.proto"),
            "syntax = \"proto3\";");

        var scripts = new SkillScriptExtractor().ExtractFromDirectory(
            tempDir.Path,
            "Local Skill",
            "MainBehavior");

        var script = scripts.Should().ContainSingle().Subject;
        script.ScriptId.Should().Be("local-skill-main");
        script.SourceFiles.Should().ContainSingle().Which.Key.Should().Be("scripts/Main.cs");
        script.ProtoFiles.Should().ContainSingle().Which.Key.Should().Be("scripts/contract.proto");
        script.EntryBehaviorTypeName.Should().Be("MainBehavior");
    }

    [Fact]
    public void SkillDiscovery_PopulatesScriptsFromSkillDirectory()
    {
        using var tempDir = new TempDirectory();
        var skillDir = Path.Combine(tempDir.Path, "skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: local-script
            scriptEntry: Local.EntryBehavior
            ---
            Use script.
            """);
        var scriptsDir = Path.Combine(skillDir, "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(
            Path.Combine(scriptsDir, "Entry.cs"),
            "public sealed class EntryBehavior {}");

        var skills = new SkillDiscovery().ScanDirectory(tempDir.Path);

        var script = skills.Should().ContainSingle().Subject.Scripts.Should().ContainSingle().Subject;
        script.ScriptId.Should().Be("local-script-entry");
        script.SourceFiles.Should().ContainSingle().Which.Key.Should().Be("scripts/Entry.cs");
        script.EntryBehaviorTypeName.Should().Be("Local.EntryBehavior");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
