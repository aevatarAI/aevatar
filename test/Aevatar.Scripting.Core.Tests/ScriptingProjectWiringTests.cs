using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Application.Queries;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Hosting.DependencyInjection;
using Aevatar.Scripting.Projection.Materialization;
using Aevatar.Scripting.Projection.Metadata;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Core.Tests;

public sealed class ScriptingProjectWiringTests
{
    [Fact]
    public void AddScriptCapability_ShouldResolveCurrentBehaviorAndProjectionServices()
    {
        var services = new ServiceCollection();
        services.AddAevatarRuntime();
        services.AddScriptCapability();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IScriptBehaviorCompiler>().Should().NotBeNull();
        provider.GetRequiredService<IScriptBehaviorArtifactResolver>().Should().NotBeNull();
        provider.GetRequiredService<IScriptBehaviorDispatcher>().Should().NotBeNull();
        provider.GetRequiredService<IScriptBehaviorRuntimeCapabilityFactory>().Should().NotBeNull();
        provider.GetRequiredService<IScriptReadModelMaterializationCompiler>().Should().NotBeNull();
        provider.GetRequiredService<IScriptNativeDocumentMaterializer>().Should().NotBeNull();
        provider.GetRequiredService<IScriptNativeGraphMaterializer>().Should().NotBeNull();
        provider.GetRequiredService<IScriptExecutionProjectionPort>().Should().NotBeNull();
        provider.GetRequiredService<IScriptReadModelQueryPort>().Should().NotBeNull();
        provider.GetRequiredService<IScriptDefinitionSnapshotPort>().Should().NotBeNull();
        provider.GetRequiredService<IScriptCatalogQueryPort>().Should().NotBeNull();
        provider.GetRequiredService<IScriptReadModelQueryApplicationService>().Should().NotBeNull();
        provider.GetRequiredService<IScriptEvolutionApplicationService>().Should().NotBeNull();
        provider.GetServices<ICurrentStateProjectionMaterializer<ScriptExecutionMaterializationContext>>()
            .Should().Contain(x => IsObservedCurrentStateMaterializerFor<ScriptReadModelProjector>(x))
            .And.Contain(x => IsObservedCurrentStateMaterializerFor<ScriptNativeDocumentProjector>(x))
            .And.Contain(x => IsObservedCurrentStateMaterializerFor<ScriptNativeGraphProjector>(x));
        provider.GetServices<ICurrentStateProjectionMaterializer<ScriptAuthorityProjectionContext>>()
            .Should().Contain(x => IsObservedCurrentStateMaterializerFor<ScriptDefinitionSnapshotProjector>(x))
            .And.Contain(x => IsObservedCurrentStateMaterializerFor<ScriptCatalogEntryProjector>(x));
        provider.GetServices<ICurrentStateProjectionMaterializer<ScriptEvolutionMaterializationContext>>()
            .Should().ContainSingle(x => IsObservedCurrentStateMaterializerFor<ScriptEvolutionReadModelProjector>(x));
        provider.GetServices<IProjectionActivationPlanProvider>()
            .Should().ContainSingle(x => x is ScriptingCommittedStateProjectionActivationPlanProvider);
    }

    private static bool IsObservedCurrentStateMaterializerFor<TProjector>(object materializer)
    {
        var type = materializer.GetType();
        return type.IsGenericType &&
               type.Name.StartsWith("ObservedCurrentStateProjectionMaterializer`", StringComparison.Ordinal) &&
               type.GenericTypeArguments.Length == 2 &&
               type.GenericTypeArguments[1] == typeof(TProjector);
    }

    [Fact]
    public void ScriptCatalogEntryDocumentMetadataProvider_ShouldDeclareOpenIndexContract()
    {
        var provider = new ScriptCatalogEntryDocumentMetadataProvider();

        provider.Metadata.IndexName.Should().Be("script-catalog-entries");
        provider.Metadata.Mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);
        provider.Metadata.Mappings.Should().NotContainKey("properties");
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }
}
