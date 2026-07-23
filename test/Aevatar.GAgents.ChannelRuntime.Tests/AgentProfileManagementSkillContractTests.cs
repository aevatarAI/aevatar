using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentProfileManagementSkillContractTests
{
    private const string SkillRelativePath = "skills/aevatar-agent-profile-management/SKILL.md";

    [Fact]
    public void Skill_source_has_supported_trigger_frontmatter()
    {
        var source = File.ReadAllText(RepositoryFile(SkillRelativePath));
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        lines[0].Should().Be("---");
        var closingDelimiter = Array.IndexOf(lines, "---", 1);
        closingDelimiter.Should().BeGreaterThan(1);

        var frontmatter = lines[1..closingDelimiter]
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => line.Split(':', 2))
            .ToDictionary(static parts => parts[0].Trim(), static parts => parts[1].Trim());

        frontmatter.Keys.Should().BeEquivalentTo("name", "description");
        frontmatter["name"].Should().Be("aevatar-agent-profile-management");
        frontmatter["description"].Should().StartWith("Use when");
        frontmatter["description"].Should().Contain("owner", Exactly.Once());
        frontmatter["description"].Should().Contain("Profile");
        frontmatter["description"].Contains("search", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        frontmatter["description"].Contains("validate", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        frontmatter["description"].Contains("publish", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void Skill_body_enforces_exact_reference_and_accepted_reconciliation_workflow()
    {
        var source = File.ReadAllText(RepositoryFile(SkillRelativePath));

        source.Should().Contain("ornn_search_skills");
        source.Should().Contain("agent_profiles");
        source.Should().Contain("skill_guid");
        source.Should().Contain("literal_version");
        source.Should().Contain("expected_name");
        source.Should().Contain("expected_publisher_id");
        Contains(source, "stable GUID").Should().BeTrue();
        Contains(source, "literal major.minor version").Should().BeTrue();
        Contains(source, "canonical name").Should().BeTrue();
        Contains(source, "publisher id").Should().BeTrue();

        var steps = new[]
        {
            Position(source, "Read the owner Profile"),
            Position(source, "ornn_search_skills"),
            Position(source, "Inspect"),
            Position(source, "upsert_skill"),
            Position(source, "Reread"),
            Position(source, "`validate`"),
            Position(source, "`publish`"),
            Position(source, "reconciles"),
        };
        steps.Should().OnlyContain(static position => position >= 0);
        steps.Should().BeInAscendingOrder();

        Contains(source, "strong ETag").Should().BeTrue();
        Contains(source, "new ETag").Should().BeTrue();
        Contains(source, "valid report").Should().BeTrue();
        Contains(source, "name-only").Should().BeTrue();
        Contains(source, "latest").Should().BeTrue();
        Contains(source, "inline skill content").Should().BeTrue();
        Contains(source, "sealed content").Should().BeTrue();
        Contains(source, "credentials").Should().BeTrue();
        source.Should().Contain("system/*");
        Contains(source, "another owner").Should().BeTrue();
        Contains(source, "another scope").Should().BeTrue();
        Contains(source, "channel binding").Should().BeTrue();
        Contains(source, "202 Accepted").Should().BeTrue();
        Contains(source, "not committed").Should().BeTrue();
    }

    [Fact]
    public void Mainnet_project_links_the_repository_skill_source_to_output_and_publish()
    {
        var project = XDocument.Load(RepositoryFile("src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj"));
        var content = project.Descendants("Content")
            .SingleOrDefault(element =>
                string.Equals(
                    (string?)element.Attribute("Include"),
                    "../../skills/aevatar-agent-profile-management/SKILL.md",
                    StringComparison.Ordinal));

        content.Should().NotBeNull();
        ((string?)content!.Attribute("Link")).Should().Be(
            "skills/aevatar-agent-profile-management/SKILL.md");
        ((string?)content.Attribute("CopyToOutputDirectory")).Should().Be("PreserveNewest");
        ((string?)content.Attribute("CopyToPublishDirectory")).Should().Be("PreserveNewest");
    }

    [Fact]
    public void Repository_has_one_authoritative_management_skill_body()
    {
        var repositoryRoot = FindRepositoryRoot();
        var skillSources = new[] { "skills", "src" }
            .Select(path => Path.Combine(repositoryRoot, path))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "SKILL.md", SearchOption.AllDirectories))
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(
                "name: aevatar-agent-profile-management",
                StringComparison.Ordinal))
            .ToArray();

        skillSources.Should().ContainSingle()
            .Which.Should().Be(RepositoryFile(SkillRelativePath));
    }

    private static int Position(string source, string marker) =>
        source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string source, string marker) =>
        source.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static string RepositoryFile(string relativePath) =>
        Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Unable to locate aevatar.slnx.");
    }
}
