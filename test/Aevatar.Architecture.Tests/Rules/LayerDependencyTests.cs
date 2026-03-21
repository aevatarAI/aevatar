using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class LayerDependencyTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

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
    public void ProjectionProviders_ShouldNot_DependOn_GAgentServiceBusiness()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection\.Providers(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.GAgentService(\..+)?"))
            .Because("Projection Providers must not depend on GAgentService business layer");
        rule.Check(Arch);
    }
}
