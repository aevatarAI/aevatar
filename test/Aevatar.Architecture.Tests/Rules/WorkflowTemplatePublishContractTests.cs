using System.Xml.Linq;

namespace Aevatar.Architecture.Tests.Rules;

public class WorkflowTemplatePublishContractTests
{
    [Theory]
    [InlineData(
        "src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj",
        "..\\..\\workflow-templates\\**\\*.yaml")]
    [InlineData(
        "src/workflow/Aevatar.Workflow.Host.Api/Aevatar.Workflow.Host.Api.csproj",
        "..\\..\\..\\workflow-templates\\**\\*.yaml")]
    public void PublishedHosts_ShouldCopyWorkflowTemplatesToPublishOutput(
        string projectPath,
        string expectedInclude)
    {
        var project = XDocument.Load(Path.Combine(FindRepositoryRoot(), projectPath));
        var templateContent = project
            .Descendants()
            .SingleOrDefault(element =>
                element.Name.LocalName == "Content" &&
                string.Equals((string?)element.Attribute("Include"), expectedInclude, StringComparison.Ordinal));

        Assert.NotNull(templateContent);
        Assert.Equal(
            "workflow-templates\\%(RecursiveDir)%(Filename)%(Extension)",
            (string?)templateContent.Attribute("Link"));
        Assert.Equal("PreserveNewest", (string?)templateContent.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)templateContent.Attribute("CopyToPublishDirectory"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
