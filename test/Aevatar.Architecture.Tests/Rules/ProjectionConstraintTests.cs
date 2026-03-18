using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class ProjectionConstraintTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    [Fact]
    public void LegacyReducerAbstractions_ShouldNot_Exist()
    {
        IArchRule rule = Types().That()
            .HaveNameContaining("IProjectionEventReducer")
            .Should().NotExist()
            .Because("reducer-era projection abstractions are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void ProjectionContextReverseLookup_ShouldNot_Exist()
    {
        IArchRule rule = MethodMembers().That()
            .HaveNameContaining("TryGetContext")
            .And().AreDeclaredIn(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection(\..+)?"))
            .Should().NotExist()
            .Because("projection context reverse lookup is forbidden; use explicit lease/session handles");
        rule.Check(Arch);
    }

    [Fact]
    public void ApplicationLayer_ShouldNot_DependOn_ProjectionDocumentStore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Application(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameContaining("IProjectionDocumentStore"))
            .Because("command-side must not depend on projection read-model stores");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingApplicationLayer_ShouldNot_DependOn_ProjectionDocumentStore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Application(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameContaining("IProjectionDocumentStore"))
            .Because("command-side must not depend on projection read-model stores");
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyProjectionListAsync_ShouldNot_Exist()
    {
        // IProjectionDocumentReader.ListAsync is a forbidden legacy pattern
        IArchRule rule = MethodMembers().That()
            .HaveName("ListAsync")
            .And().AreDeclaredIn(
                Types().That().HaveNameContaining("IProjectionDocumentReader"))
            .Should().NotExist()
            .Because("IProjectionDocumentReader.ListAsync is a legacy pattern; use specific query contracts")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyCommittedStateProjectionSemantics_ShouldNot_Exist()
    {
        // Legacy projection/readmodel semantic identifiers
        IArchRule rule = Types().That()
            .HaveNameMatching("^(CommittedStateProjectionEnvelope|CommittedStateReadModelRoot|ICommittedStateProjectionBindingResolver)$")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotExist()
            .Because("legacy committed state projection/readmodel semantic identifiers are forbidden");
        rule.Check(Arch);
    }
}
