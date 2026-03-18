using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class SerializationConstraintTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    [Fact]
    public void FoundationCore_ShouldNot_DependOn_NewtonsoftJson()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Foundation.Core")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Newtonsoft\.Json(\..+)?"))
            .Because("Core/Domain layers must use Protobuf serialization, not Newtonsoft.Json");
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowCore_ShouldNot_DependOn_NewtonsoftJson()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Workflow.Core")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Newtonsoft\.Json(\..+)?"))
            .Because("Core/Domain layers must use Protobuf serialization, not Newtonsoft.Json");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingCore_ShouldNot_DependOn_NewtonsoftJson()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Newtonsoft\.Json(\..+)?"))
            .Because("Core/Domain layers must use Protobuf serialization, not Newtonsoft.Json");
        rule.Check(Arch);
    }

    [Fact]
    public void AICore_ShouldNot_DependOn_NewtonsoftJson()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.AI\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Newtonsoft\.Json(\..+)?"))
            .Because("Core/Domain layers must use Protobuf serialization, not Newtonsoft.Json");
        rule.Check(Arch);
    }

    [Fact]
    public void FoundationCore_ShouldNot_DependOn_SystemTextJsonSerializer()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Foundation.Core")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("System.Text.Json.JsonSerializer"))
            .Because("Core/Domain layers must use Protobuf serialization, not System.Text.Json");
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowCore_ShouldNot_DependOn_SystemTextJsonSerializer()
    {
        IArchRule rule = Types().That()
            .ResideInNamespace("Aevatar.Workflow.Core")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("System.Text.Json.JsonSerializer"))
            .Because("Core/Domain layers must use Protobuf serialization, not System.Text.Json");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingCore_ShouldNot_DependOn_SystemTextJsonSerializer()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("System.Text.Json.JsonSerializer"))
            .Because("Core/Domain layers must use Protobuf serialization, not System.Text.Json");
        rule.Check(Arch);
    }

    [Fact]
    public void AICore_ShouldNot_DependOn_SystemTextJsonSerializer()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.AI\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveFullName("System.Text.Json.JsonSerializer"))
            .Because("Core/Domain layers must use Protobuf serialization, not System.Text.Json");
        rule.Check(Arch);
    }
}
