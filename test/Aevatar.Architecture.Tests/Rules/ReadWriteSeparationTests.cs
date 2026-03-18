using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class ReadWriteSeparationTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    [Fact]
    public void WorkflowCore_ShouldNot_DependOn_OrleansStreams()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Workflow.Core")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Orleans\.Streams(\..+)?"))
            .Because("Core layers must be runtime-neutral and not depend on Orleans.Streams");
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowCore_ShouldNot_DependOn_IGrainFactory()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Workflow.Core")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("Orleans.IGrainFactory"))
            .Because("Core layers must be runtime-neutral and not depend on IGrainFactory");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingCore_ShouldNot_DependOn_OrleansStreams()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Scripting.Core")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Orleans\.Streams(\..+)?"))
            .Because("Core layers must be runtime-neutral and not depend on Orleans.Streams");
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowApplication_ShouldNot_DependOn_OrleansStreams()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Application(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Orleans\.Streams(\..+)?"))
            .Because("Application layers must be runtime-neutral and not depend on Orleans.Streams");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingApplication_ShouldNot_DependOn_OrleansStreams()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Application(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Orleans\.Streams(\..+)?"))
            .Because("Application layers must be runtime-neutral and not depend on Orleans.Streams");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingCore_ShouldNot_DependOn_IGrainFactory()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("Orleans.IGrainFactory"))
            .Because("Core layers must be runtime-neutral and not depend on IGrainFactory");
        rule.Check(Arch);
    }

    [Fact]
    public void AICore_ShouldNot_DependOn_OrleansStreams()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.AI\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Orleans\.Streams(\..+)?"))
            .Because("Core layers must be runtime-neutral and not depend on Orleans.Streams");
        rule.Check(Arch);
    }

    [Fact]
    public void AICore_ShouldNot_DependOn_IGrainFactory()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.AI\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("Orleans.IGrainFactory"))
            .Because("Core layers must be runtime-neutral and not depend on IGrainFactory");
        rule.Check(Arch);
    }

    [Fact]
    public void FoundationCore_ShouldNot_DependOn_OrleansStreams()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Foundation.Core")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Orleans\.Streams(\..+)?"))
            .Because("Core layers must be runtime-neutral and not depend on Orleans.Streams");
        rule.Check(Arch);
    }

    [Fact]
    public void FoundationCore_ShouldNot_DependOn_IClusterClient()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Foundation.Core")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("Orleans.IClusterClient"))
            .Because("Core layers must be runtime-neutral and not depend on IClusterClient");
        rule.Check(Arch);
    }
}
