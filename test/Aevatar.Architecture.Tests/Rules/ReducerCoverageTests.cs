using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class ReducerCoverageTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    [Fact]
    public void ConcreteReducers_Should_InheritApprovedBaseClasses()
    {
        // All concrete Reducer/Materializer implementations should inherit from approved bases
        // This replaces the shell guard checking reducer inheritance
        IArchRule rule = Classes().That()
            .HaveNameMatching(".*Projector$")
            .And().AreNotAbstract()
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().BeAssignableTo(
                Types().That().HaveNameMatching(".*IProjectionMaterializer.*"))
            .OrShould().BeAssignableTo(
                Types().That().HaveNameMatching(".*IProjectionProjector.*"))
            .Because("concrete projectors must implement IProjectionMaterializer or IProjectionProjector");
        rule.Check(Arch);
    }
}
