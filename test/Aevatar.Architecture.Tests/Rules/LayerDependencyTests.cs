using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class LayerDependencyTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    [Fact]
    public void StudioApplication_ShouldNot_DependOn_GAgentServiceApplication()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "Aevatar.Studio.Application", "Aevatar.Studio.Application.csproj");
        var project = File.ReadAllText(projectPath);

        Assert.DoesNotContain(
            "Aevatar.GAgentService.Application.csproj",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AevatarInvocationToolProvider_ShouldNot_DependOn_GAgentServiceApplication()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "Aevatar.AI.ToolProviders.AevatarInvocation", "Aevatar.AI.ToolProviders.AevatarInvocation.csproj");
        var project = File.ReadAllText(projectPath);

        Assert.DoesNotContain(
            "Aevatar.GAgentService.Application.csproj",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GAgentServiceAbstractions_ShouldNot_Reference_PresentationProjects()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "platform", "Aevatar.GAgentService.Abstractions", "Aevatar.GAgentService.Abstractions.csproj");
        var project = File.ReadAllText(projectPath);

        Assert.DoesNotContain(
            "Aevatar.Presentation.",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Presentation.AGUI",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "Aevatar.AGUI.Contracts",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledAgents_ShouldUseSkillsAbstractionWithoutDependingOnOrnnProvider()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "agents", "Aevatar.GAgents.Scheduled", "Aevatar.GAgents.Scheduled.csproj");
        var project = File.ReadAllText(projectPath);

        Assert.Contains(
            "Aevatar.AI.ToolProviders.Skills.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "Aevatar.Workflow.Application.Abstractions.csproj",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Aevatar.AI.ToolProviders.Ornn.csproj",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NyxidChat_ShouldOnlyDependOnStudioApplicationAbstractions()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "agents", "Aevatar.GAgents.NyxidChat", "Aevatar.GAgents.NyxidChat.csproj");
        var project = File.ReadAllText(projectPath);

        Assert.Contains(
            "Aevatar.Studio.Application.Abstractions.csproj",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Aevatar.Studio.Application.csproj",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Aevatar.Studio.Infrastructure.csproj",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowCore_ShouldNot_DependOn_AICore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Workflow.Core")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespace("Aevatar.AI.Core"))
            .Because("Workflow.Core must not depend on AI.Core");
        rule.Check(Arch);
    }

    [Fact]
    public void Abstractions_ShouldNot_DependOn_Core()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Foundation.Abstractions")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespace("Aevatar.Foundation.Core"))
            .Because("Abstractions must not depend on Core implementations");
        rule.Check(Arch);
    }

    [Fact]
    public void Abstractions_ShouldNot_DependOn_Infrastructure()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Scripting.Abstractions")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Scripting\.Infrastructure(\..+)?"))
            .Because("Abstractions must not depend on Infrastructure");
        rule.Check(Arch);
    }

    [Fact]
    public void Core_ShouldNot_DependOn_Infrastructure()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Foundation.Core")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Foundation\.Runtime\.Implementations(\..+)?"))
            .Because("Core must not depend on Infrastructure implementations");
        rule.Check(Arch);
    }

    [Fact]
    public void Core_ShouldNot_DependOn_Hosting()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Foundation.Core")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Foundation\.Runtime\.Hosting(\..+)?"))
            .Because("Core must not depend on Hosting");
        rule.Check(Arch);
    }

    [Fact]
    public void ProjectionProviders_ShouldNot_DependOn_WorkflowBusiness()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection\.Providers(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Workflow(\..+)?"))
            .Because("Projection Providers must not depend on Workflow business layer");
        rule.Check(Arch);
    }

    [Fact]
    public void ProjectionProviders_ShouldNot_DependOn_AIBusiness()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection\.Providers(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.AI(\..+)?"))
            .Because("Projection Providers must not depend on AI business layer");
        rule.Check(Arch);
    }

    [Fact]
    public void ProjectionProviders_ShouldNot_DependOn_ScriptingBusiness()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection\.Providers(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Scripting(\..+)?"))
            .Because("Projection Providers must not depend on Scripting business layer");
        rule.Check(Arch);
    }

    [Fact]
    public void AIAbstractions_ShouldNot_DependOn_AICore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.AI\.Abstractions(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.AI\.Core(\..+)?"))
            .Because("Abstractions must not depend on Core implementations");
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowAbstractions_ShouldNot_DependOn_WorkflowCore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Abstractions(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Workflow\.Core(\..+)?"))
            .Because("Abstractions must not depend on Core implementations");
        rule.Check(Arch);
    }

    [Fact]
    public void CqrsAbstractions_ShouldNot_DependOn_CqrsCore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.CQRS\.Core\.Abstractions(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.CQRS\.Core(\..+)?")
                    .And().DoNotResideInNamespaceMatching(@"Aevatar\.CQRS\.Core\.Abstractions(\..+)?"))
            .Because("CQRS Abstractions must not depend on CQRS Core implementations");
        rule.Check(Arch);
    }

    [Fact]
    public void ProjectionAbstractions_ShouldNot_DependOn_ProjectionCore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection\.Core\.Abstractions(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection\.Core(\..+)?")
                    .And().DoNotResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection\.Core\.Abstractions(\..+)?"))
            .Because("Projection Abstractions must not depend on Projection Core implementations");
        rule.Check(Arch);
    }

    [Fact]
    public void ProjectionCoreAbstractionsProject_ShouldNot_Reference_FoundationCore()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "Aevatar.CQRS.Projection.Core.Abstractions", "Aevatar.CQRS.Projection.Core.Abstractions.csproj");
        var project = File.ReadAllText(projectPath);

        Assert.DoesNotContain(
            "Aevatar.Foundation.Core.csproj",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionProviders_ShouldNot_DependOn_GAgentServiceBusiness()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection\.Providers(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.GAgentService(\..+)?"))
            .Because("Projection Providers must not depend on GAgentService business layer");
        rule.Check(Arch);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
