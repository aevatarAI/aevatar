using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.Bootstrap.Extensions.AI;
using Aevatar.Bootstrap.Extensions.AI.OrnnPublishing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Bootstrap.Tests;

public sealed class OrnnPublishValidationRegistrationTests
{
    [Fact]
    public void AddAevatarAIFeatures_WhenOrnnEnabled_ShouldRegisterConcretePublishValidatorsAtCompositionBoundary()
    {
        var services = new ServiceCollection();

        services.AddAevatarAIFeatures(
            new ConfigurationBuilder().Build(),
            options => options.EnableOrnnSkills = true);

        var descriptors = services
            .Where(x => x.ServiceType == typeof(IOrnnSkillPublishAssetValidator))
            .Select(x => x.ImplementationType)
            .ToArray();

        descriptors.Should().Contain(typeof(WorkflowOrnnSkillPublishAssetValidator));
        descriptors.Should().Contain(typeof(ScriptOrnnSkillPublishAssetValidator));
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenOrnnDisabled_ShouldNotRegisterPublishValidators()
    {
        var services = new ServiceCollection();

        services.AddAevatarAIFeatures(
            new ConfigurationBuilder().Build(),
            options => options.EnableOrnnSkills = false);

        services.Should().NotContain(x => x.ServiceType == typeof(IOrnnSkillPublishAssetValidator));
    }
}
