using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class CqrsBoundaryTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    [Fact]
    public void QueryPorts_ShouldNot_DependOn_ActivationService()
    {
        // Query/Read 路径禁止触发 projection priming
        IArchRule rule = Types().That()
            .HaveNameMatching(".*Query(Reader|Port|Service).*")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameContaining("ActivationService"))
            .Because("query paths must not trigger projection priming or activation");
        rule.Check(Arch);
    }

    [Fact]
    public void QueryPorts_ShouldNot_DependOn_ProjectionLifecycle()
    {
        IArchRule rule = Types().That()
            .HaveNameMatching(".*Query(Reader|Port|Service).*")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameContaining("ProjectionLifecycle"))
            .Because("query paths must not control projection lifecycle");
        rule.Check(Arch);
    }

    [Fact]
    public void CommandSide_ShouldNot_DependOn_DocumentReader()
    {
        // command-side 禁止引用 readmodel store
        IArchRule rule = Types().That()
            .HaveNameMatching(".*Command(Target|Dispatch|Binder|Resolver).*")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameContaining("IProjectionDocumentReader"))
            .Because("command-side must not depend on projection document readers");
        rule.Check(Arch);
    }

    [Fact]
    public void Materializers_ShouldNot_DependOn_DocumentReader()
    {
        // current-state projector 禁止回读 readmodel
        IArchRule rule = Types().That()
            .HaveNameMatching(".*CurrentState.*Projector.*")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameContaining("IProjectionDocumentReader"))
            .Because("current-state projectors must not read back their own read models");
        rule.Check(Arch);
    }

    [Fact]
    public void WorkflowCore_ShouldNot_DependOn_CqrsProjectionCore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Workflow\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection(\..+)?"))
            .Because("Workflow.Core must not depend on CQRS Projection implementation");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingCore_ShouldNot_DependOn_CqrsProjectionCore()
    {
        IArchRule rule = Types().That()
            .ResideInNamespaceMatching(@"Aevatar\.Scripting\.Core(\..+)?")
            .Should().NotDependOnAny(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.CQRS\.Projection(\..+)?"))
            .Because("Scripting.Core must not depend on CQRS Projection implementation");
        rule.Check(Arch);
    }

    [Fact]
    public void QueryReadPorts_ShouldNot_DependOn_IEventStore()
    {
        // Read/query paths must not access IEventStore directly.
        // Query resolution must come from read models, not event replay.
        IArchRule rule = Types().That()
            .HaveNameMatching(".*(QueryService|QueryReader|ReadPort|BindingReader|SnapshotPort).*")
            .And().ResideInNamespaceMatching(@"Aevatar\.(Scripting|Workflow)\.(Application|Infrastructure|Projection)(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameMatching("^IEventStore$"))
            .Because("read/query paths must not replay committed facts from IEventStore")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void QueryReadPorts_ShouldNot_InlineMaterialize()
    {
        // Read/query paths must not materialize or mutate read models inline.
        // Projection materialization must stay off the query call stack.
        IArchRule rule = Types().That()
            .HaveNameMatching(".*(QueryService|QueryReader|ReadPort|BindingReader).*")
            .And().ResideInNamespaceMatching(@"Aevatar\.(Scripting|Workflow)\.(Application|Infrastructure|Projection)(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameContaining("IProjectionDocumentWriter"))
            .Because("read/query paths must not write to projection stores inline")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyDirectQueryTypes_ShouldNot_Exist()
    {
        // Forbid generic actor query/reply and stream request-reply patterns
        IArchRule rule = Types().That()
            .HaveNameMatching("^(IStreamRequestReplyClient|StreamRequestReplyClient|I?ActorQueryPort|ActorQueryReply)$")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotExist()
            .Because("generic actor query/reply and stream request-reply patterns are forbidden; use read models");
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyDirectQueryExpansionTypes_ShouldNot_Exist()
    {
        // cqrs_eventsourcing_boundary_guard.sh: expanded legacy direct query patterns
        IArchRule rule = Types().That()
            .HaveNameMatching("^(RuntimeStreamRequestReplyClient|RuntimeScriptActorQueryClient|RuntimeScriptCatalogQueryService|RuntimeScriptDefinitionSnapshotPort|ScriptActorQueryEnvelopeFactory|ScriptActorQueryRouteConventions|RuntimeWorkflowQueryClient|RuntimeWorkflowActorBindingReader|WorkflowActorBindingQueryEnvelopeFactory)$")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotExist()
            .Because("direct actor query/request-reply types must not expand; use read models or eventized continuation");
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyQueryRequestedEvents_ShouldNot_Exist()
    {
        // cqrs_eventsourcing_boundary_guard.sh: Query*RequestedEvent patterns
        IArchRule rule = Types().That()
            .HaveNameMatching("^Query[A-Za-z0-9_]+RequestedEvent$")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotExist()
            .Because("Query*RequestedEvent patterns are legacy direct actor query contracts; use read models");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingWritePath_ShouldNot_DependOn_AuthorityActivationPorts()
    {
        IArchRule rule = Types().That()
            .HaveNameMatching("^(RuntimeScriptDefinitionCommandService|RuntimeScriptCatalogCommandService|ScriptBehaviorRuntimeCapabilities|ScriptBehaviorRuntimeCapabilityFactory)$")
            .And().ResideInNamespaceMatching(@"Aevatar\.Scripting\.(Application|Infrastructure)(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameMatching("^(IScriptAuthorityReadModelActivationPort|IScriptAuthorityProjectionPrimingPort)$"))
            .Because("scripting write paths must not control authority read-model lifecycle");
        rule.Check(Arch);
    }

    [Fact]
    public void ScriptingCatalogWritePath_ShouldNot_DependOn_QueryPorts()
    {
        IArchRule rule = Types().That()
            .HaveName("RuntimeScriptCatalogCommandService")
            .And().ResideInNamespaceMatching(@"Aevatar\.Scripting\.Infrastructure(\..+)?")
            .Should().NotDependOnAny(
                Types().That().HaveNameMatching("^(IScriptCatalogQueryPort|IScriptReadModelQueryPort)$"))
            .Because("catalog command success must not depend on query-port catch-up");
        rule.Check(Arch);
    }
}
