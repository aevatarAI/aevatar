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
            .Should().Contain(x => x is ScriptReadModelProjector)
            .And.Contain(x => x is ScriptNativeDocumentProjector)
            .And.Contain(x => x is ScriptNativeGraphProjector);
        provider.GetServices<ICurrentStateProjectionMaterializer<ScriptAuthorityProjectionContext>>()
            .Should().Contain(x => x is ScriptDefinitionSnapshotProjector)
            .And.Contain(x => x is ScriptCatalogEntryProjector);
        provider.GetServices<ICurrentStateProjectionMaterializer<ScriptEvolutionMaterializationContext>>()
            .Should().ContainSingle(x => x is ScriptEvolutionReadModelProjector);
    }

    [Fact]
    public void ScriptCatalogEntryDocumentMetadataProvider_ShouldDeclareSortableTimestampMappings()
    {
        var provider = new ScriptCatalogEntryDocumentMetadataProvider();

        provider.Metadata.IndexName.Should().Be("script-catalog-entries");
        provider.Metadata.Mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);
        provider.Metadata.Mappings.Should().ContainKey("properties");

        var properties = provider.Metadata.Mappings["properties"].Should()
            .BeAssignableTo<IReadOnlyDictionary<string, object?>>()
            .Subject;
        properties.Should().ContainKey("created_at_utc_value");
        properties.Should().ContainKey("updated_at_utc_value");
        GetFieldType(properties, "created_at_utc_value").Should().Be("date");
        GetFieldType(properties, "updated_at_utc_value").Should().Be("date");
        provider.Metadata.Settings.Should().BeEmpty();
        provider.Metadata.Aliases.Should().BeEmpty();
    }

    private static object? GetFieldType(
        IReadOnlyDictionary<string, object?> properties,
        string fieldName)
    {
        var field = properties[fieldName].Should()
            .BeAssignableTo<IReadOnlyDictionary<string, object?>>()
            .Subject;
        return field["type"];
    }
}
