using ArchUnitNET.Fluent;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Aevatar.Architecture.Tests.Rules;

public class ForbiddenPatternTests
{
    private static readonly ArchitectureModel Arch = ArchitectureTestBase.ProductionArchitecture;

    [Fact]
    public void MiddleLayer_ShouldNot_Declare_IdMappingDictionaries()
    {
        // Application/Projection middle layers must not maintain entity/actor/run/session ID
        // to context or fact-state in-process mappings. This is a simplified version that
        // checks for ConcurrentDictionary field declarations in middle-layer types
        // that are not InMemory infrastructure.
        var middleLayerNamespacePatterns = new[]
        {
            @"Aevatar\.CQRS\.Projection\.Core(\..+)?",
            @"Aevatar\.Foundation\.Projection(\..+)?",
            @"Aevatar\.AI\.Projection(\..+)?",
            @"Aevatar\.Workflow\.Projection(\..+)?",
            @"Aevatar\.Workflow\.Application(\..+)?",
            @"Aevatar\.Scripting\.Application(\..+)?",
            @"Aevatar\.Scripting\.Projection(\..+)?",
        };

        foreach (var pattern in middleLayerNamespacePatterns)
        {
            IArchRule rule = FieldMembers().That()
                .AreDeclaredIn(
                    Types().That().ResideInNamespaceMatching(pattern)
                        .And().DoNotHaveNameContaining("InMemory"))
                .And().HaveNameContaining("ConcurrentDictionary")
                .Should().NotExist()
                .Because($"middle-layer ID-mapping in-memory state is forbidden in {pattern}");
            rule.Check(Arch);
        }
    }

    [Fact]
    public void Production_ShouldNot_Have_LegacyBindingResolver()
    {
        // 禁止 BindingResolver 投影路由
        IArchRule rule = Types().That()
            .HaveNameContaining("BindingResolver")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotExist()
            .Because("legacy BindingResolver projection routing is forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void Production_ShouldNot_Have_ExecuteDeclaredQueryAsync()
    {
        IArchRule rule = MethodMembers().That()
            .HaveName("ExecuteDeclaredQueryAsync")
            .And().AreDeclaredIn(
                Types().That().ResideInNamespaceMatching(@"Aevatar(\..+)?"))
            .Should().NotExist()
            .Because("legacy declared query execution is forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void Production_ShouldNot_Have_AddMakerCapability()
    {
        IArchRule rule = MethodMembers().That()
            .HaveName("AddMakerCapability")
            .And().AreDeclaredIn(
                Types().That().ResideInNamespaceMatching(@"Aevatar(\..+)?"))
            .Should().NotExist()
            .Because("standalone Maker capability registration is forbidden; use AddAevatarPlatform");
        rule.Check(Arch);
    }

    [Fact]
    public void Production_ShouldNot_Have_ConfirmStateAsync()
    {
        IArchRule rule = MethodMembers().That()
            .HaveNameMatching("^(ConfirmStateAsync|ConfirmDerivedEventsAsync)$")
            .And().AreDeclaredIn(
                Types().That().ResideInNamespaceMatching(@"Aevatar(\..+)?"))
            .Should().NotExist()
            .Because("legacy state confirmation methods are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void Production_ShouldNot_Have_MakeGenericType_In_EventSourcing()
    {
        // Reflection-based ES binding forbidden
        IArchRule rule = Types().That()
            .HaveNameContaining("EventSourceBinding")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotExist()
            .Because("reflection-based event source binding is forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void Production_ShouldNot_Have_TryUnpackState()
    {
        // EventSourcingBehavior must not unpack TState snapshots from persisted events
        IArchRule rule = MethodMembers().That()
            .HaveNameMatching("^(TryUnpack|Unpack)$")
            .And().AreDeclaredIn(
                Types().That().HaveNameContaining("EventSourcingBehavior"))
            .Should().NotExist()
            .Because("EventSourcingBehavior must not unpack TState snapshots from persisted events")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void RuntimeActorLayer_ShouldNot_Have_RelayGraph()
    {
        // Runtime actor layer must not execute relay graph traversal
        IArchRule rule = MethodMembers().That()
            .HaveNameMatching("^(ListBySourceAsync|TryBuildForwardedEnvelope)$")
            .And().AreDeclaredIn(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Foundation\.Runtime\.Actor(\..+)?"))
            .Should().NotExist()
            .Because("relay graph execution must stay in stream/message-queue infrastructure, not runtime actors")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void HostInfra_ShouldNot_Have_AddCqrsCore()
    {
        // Host/Infrastructure must not directly call AddCqrsCore; must use Aevatar.CQRS.Runtime.Hosting
        IArchRule rule = MethodMembers().That()
            .HaveName("AddCqrsCore")
            .And().AreDeclaredIn(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.(Mainnet\.Host|Workflow\.Host|Workflow\.Infrastructure)(\..+)?"))
            .Should().NotExist()
            .Because("direct AddCqrsCore wiring in hosts/infrastructure is forbidden; use Aevatar.CQRS.Runtime.Hosting")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void Production_ShouldNot_Have_LegacyScriptingProjectionTypes()
    {
        // Legacy scripting readmodel projection types are forbidden
        IArchRule rule = Types().That()
            .HaveNameMatching("^(IScriptingProjectionArtifactResolver|ScriptingProjectionArtifactResolver)$")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotExist()
            .Because("legacy scripting projection artifact resolvers are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void Production_ShouldNot_Have_LegacyScriptingDeclaredQuery()
    {
        // Legacy scripting declared-query contracts
        IArchRule rule = Types().That()
            .HaveNameMatching("^(I?DeclaredQuery(Contract|Service|Port)|DeclaredQuery(Requested|Responded))$")
            .And().ResideInNamespaceMatching(@"Aevatar(\..+)?")
            .Should().NotExist()
            .Because("legacy declared-query contracts are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void Production_ShouldNot_Have_ProjectReadModel_Method()
    {
        // Legacy scripting readmodel projection method
        IArchRule rule = MethodMembers().That()
            .HaveName("ProjectReadModel")
            .And().AreDeclaredIn(
                Types().That().ResideInNamespaceMatching(@"Aevatar\.Scripting(\..+)?"))
            .Should().NotExist()
            .Because("legacy scripting ProjectReadModel method is forbidden")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyScriptLifecycleFacade_ShouldNot_Exist()
    {
        // scripting_interaction_boundary_guard.sh: lifecycle total-port facades are forbidden
        IArchRule rule = Types().That()
            .HaveNameMatching("^(IScriptLifecyclePort|RuntimeScriptLifecyclePort|ScriptActorCommandPortBase|RuntimeScriptDefinitionLifecycleService|RuntimeScriptExecutionLifecycleService)$")
            .And().ResideInNamespaceMatching(@"Aevatar\.Scripting(\..+)?")
            .Should().NotExist()
            .Because("legacy scripting lifecycle total-port facades and direct-dispatch adapter bases are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyScriptActorRequestWrappers_ShouldNot_Exist()
    {
        // scripting_interaction_boundary_guard.sh: legacy ActorRequest/Adapter wrappers are forbidden
        IArchRule rule = Types().That()
            .HaveNameMatching("^(UpsertScriptDefinitionActorRequest|RunScriptActorRequest|PromoteScriptRevisionActorRequest|RollbackScriptRevisionActorRequest)(Adapter)?$")
            .And().ResideInNamespaceMatching(@"Aevatar\.Scripting(\..+)?")
            .Should().NotExist()
            .Because("legacy ActorRequest/ActorRequestAdapter command wrappers are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void LegacyScriptQueryAdapters_ShouldNot_Exist()
    {
        // scripting_interaction_boundary_guard.sh: per-query request adapter wrappers are forbidden
        IArchRule rule = Types().That()
            .HaveNameMatching("^(QueryScriptDefinitionSnapshotRequestAdapter|QueryScriptCatalogEntryRequestAdapter|QueryScriptEvolutionDecisionRequestAdapter)$")
            .And().ResideInNamespaceMatching(@"Aevatar\.Scripting(\..+)?")
            .Should().NotExist()
            .Because("legacy per-query request adapter wrappers are forbidden");
        rule.Check(Arch);
    }

    [Fact]
    public void EvolutionInteractionService_ShouldNot_DependOn_ManualOrchestration()
    {
        // scripting_interaction_boundary_guard.sh: must not manually orchestrate
        // projection/fallback/dispatch concerns
        IArchRule rule = Types().That()
            .HaveNameContaining("RuntimeScriptEvolutionInteractionService")
            .Should().NotDependOnAny(
                Types().That().HaveNameMatching("^(IScriptEvolutionProjectionPort|IScriptEvolutionDecisionFallbackPort|RuntimeScriptActorAccessor|IScriptingActorAddressResolver)$"))
            .Because("evolution interaction service must not manually orchestrate projection/fallback/dispatch concerns")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }

    [Fact]
    public void RuntimeProvisioning_ShouldNot_DependOn_SnapshotPort()
    {
        // scripting_runtime_snapshot_guard.sh: provisioning must not query/poll snapshot read models
        IArchRule rule = Types().That()
            .HaveNameContaining("RuntimeScriptProvisioningService")
            .Should().NotDependOnAny(
                Types().That().HaveNameContaining("IScriptDefinitionSnapshotPort"))
            .Because("runtime script provisioning must not query/poll definition snapshot read models")
            .WithoutRequiringPositiveResults();
        rule.Check(Arch);
    }
}
