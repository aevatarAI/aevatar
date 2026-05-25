using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ElasticsearchProjectionConfigurationTests
{
    [Fact]
    public void IsEnabled_ReturnsFalse_WhenConfigurationIsNull()
    {
        var logger = Substitute.For<ILogger>();
        ElasticsearchProjectionConfiguration.IsEnabled(null, logger).Should().BeFalse();
        // No diagnostics emitted when configuration is missing — caller is in a unit-test composition.
        logger.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void IsEnabled_ReturnsTrue_WhenExplicitFlagIsTrue()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
        });
        ElasticsearchProjectionConfiguration.IsEnabled(configuration).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_HonorsCaseInsensitiveExplicitFlag()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "TRUE",
        });
        ElasticsearchProjectionConfiguration.IsEnabled(configuration).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenExplicitFlagIsFalse()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            // Even if endpoints are populated, the explicit "false" wins.
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
        });
        ElasticsearchProjectionConfiguration.IsEnabled(configuration).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_AutoDetectsTrue_WhenEndpointsArePresentAndFlagAbsent()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
        });
        ElasticsearchProjectionConfiguration.IsEnabled(configuration).Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_LogsWarning_WhenConfigurationPresentButNoFlagOrEndpoint()
    {
        var configuration = BuildConfiguration(new()
        {
            // Section exists (path is reachable) but neither Enabled nor Endpoints is populated.
            ["Projection:Document:Providers:Elasticsearch:IndexPrefix"] = "aevatar-test",
        });
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        ElasticsearchProjectionConfiguration.IsEnabled(configuration, logger, "TestStore").Should().BeFalse();

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void IsEnabled_WritesConsoleError_WhenLoggerIsNullAndEndpointsEmpty()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:IndexPrefix"] = "aevatar-test",
        });

        // SCE composition runs before the host builds its logger pipeline, so
        // the helper falls back to Console.Error to keep operator visibility
        // (matches the pre-helper Console.Error.WriteLine behavior).
        var capturedStderr = new StringWriter();
        var originalStderr = Console.Error;
        Console.SetError(capturedStderr);
        try
        {
            ElasticsearchProjectionConfiguration
                .IsEnabled(configuration, logger: null, "TestStore")
                .Should().BeFalse();
        }
        finally
        {
            Console.SetError(originalStderr);
        }

        capturedStderr.ToString().Should().Contain("TestStore");
        capturedStderr.ToString().Should().Contain("Elasticsearch is not configured");
        capturedStderr.ToString().Should().Contain("InMemory");
    }

    [Fact]
    public void IsEnabled_DoesNotWriteConsoleError_WhenLoggerIsProvided()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:IndexPrefix"] = "aevatar-test",
        });
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var capturedStderr = new StringWriter();
        var originalStderr = Console.Error;
        Console.SetError(capturedStderr);
        try
        {
            ElasticsearchProjectionConfiguration
                .IsEnabled(configuration, logger, "TestStore")
                .Should().BeFalse();
        }
        finally
        {
            Console.SetError(originalStderr);
        }

        // Logger received the warning; Console.Error must stay clean so
        // structured-log consumers don't get duplicate entries.
        capturedStderr.ToString().Should().BeEmpty();
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void IsEnabled_DoesNotLog_WhenEndpointsArePopulated()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://localhost:9200",
        });
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        ElasticsearchProjectionConfiguration.IsEnabled(configuration, logger).Should().BeTrue();
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void IsEnabled_Throws_WhenExplicitFlagIsInvalid()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "sometimes",
        });

        Action act = () => ElasticsearchProjectionConfiguration.IsEnabled(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Invalid boolean value 'sometimes'.");
    }

    [Fact]
    public void BindOptions_NullConfiguration_Throws()
    {
        Action act = () => ElasticsearchProjectionConfiguration.BindOptions(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BindOptions_PopulatesOptionsFromSection()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://es-1:9200",
            ["Projection:Document:Providers:Elasticsearch:Endpoints:1"] = "http://es-2:9200",
            ["Projection:Document:Providers:Elasticsearch:IndexPrefix"] = "aevatar-test",
            ["Projection:Document:Providers:Elasticsearch:RequestTimeoutMs"] = "5000",
            ["Projection:Document:Providers:Elasticsearch:Username"] = "elastic",
            ["Projection:Document:Providers:Elasticsearch:Password"] = "secret",
        });

        var options = ElasticsearchProjectionConfiguration.BindOptions(configuration);

        options.Endpoints.Should().BeEquivalentTo(new[] { "http://es-1:9200", "http://es-2:9200" });
        options.IndexPrefix.Should().Be("aevatar-test");
        options.RequestTimeoutMs.Should().Be(5000);
        options.Username.Should().Be("elastic");
        options.Password.Should().Be("secret");
    }

    [Fact]
    public void BindOptions_WithEmptySection_ReturnsDefaults()
    {
        var configuration = BuildConfiguration(new());
        var options = ElasticsearchProjectionConfiguration.BindOptions(configuration);

        options.Should().NotBeNull();
        options.IndexPrefix.Should().Be("aevatar");
        options.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_SelectsElasticsearch_WhenExplicitlyEnabledAndInMemoryDefaultIsDisabled()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://es:9200",
        });

        var selection = ProjectionDocumentProviderConfiguration.Resolve(configuration, "TestCapability");

        selection.Kind.Should().Be(ProjectionDocumentProviderKind.Elasticsearch);
        selection.ElasticsearchEnabled.Should().BeTrue();
        selection.InMemoryEnabled.Should().BeFalse();
    }

    [Fact]
    public void Resolve_SelectsInMemory_WhenElasticsearchDisabledAndInMemoryDefaultIsEnabled()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
        });

        var selection = ProjectionDocumentProviderConfiguration.Resolve(configuration, "TestCapability");

        selection.Kind.Should().Be(ProjectionDocumentProviderKind.InMemory);
        selection.ElasticsearchEnabled.Should().BeFalse();
        selection.InMemoryEnabled.Should().BeTrue();
    }

    [Fact]
    public void Resolve_Throws_WhenBothDocumentProvidersAreEnabled()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
            ["Projection:Document:Providers:InMemory:Enabled"] = "true",
        });

        Action act = () => ProjectionDocumentProviderConfiguration.Resolve(configuration, "TestCapability");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Exactly one document projection provider must be enabled for TestCapability.*");
    }

    [Fact]
    public void Resolve_Throws_WhenInMemoryIsSelectedAndDeniedByPolicy()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Policies:DenyInMemoryDocumentReadStore"] = "true",
        });

        Action act = () => ProjectionDocumentProviderConfiguration.Resolve(configuration, "TestCapability");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("InMemory document provider is not allowed by projection policy.*");
    }

    [Fact]
    public void Resolve_Throws_WhenInMemoryIsSelectedInProduction()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Policies:Environment"] = "Production",
        });

        Action act = () => ProjectionDocumentProviderConfiguration.Resolve(configuration, "TestCapability");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("InMemory document provider is not allowed by projection policy.*");
    }

    [Fact]
    public void Resolve_Throws_WhenInMemoryFlagIsInvalid()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "false",
            ["Projection:Document:Providers:InMemory:Enabled"] = "maybe",
        });

        Action act = () => ProjectionDocumentProviderConfiguration.Resolve(configuration, "TestCapability");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Invalid boolean value 'maybe'.");
    }

    [Fact]
    public void BindRequiredElasticsearchOptions_UsesSharedBindingAndRequiresEndpoints()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Endpoints:0"] = "http://es:9200",
            ["Projection:Document:Providers:Elasticsearch:IndexPrefix"] = "iter86",
        });

        var options = ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration);

        options.Endpoints.Should().BeEquivalentTo(new[] { "http://es:9200" });
        options.IndexPrefix.Should().Be("iter86");
    }

    [Fact]
    public void BindRequiredElasticsearchOptions_Throws_WhenEnabledButEndpointsAreEmpty()
    {
        var configuration = BuildConfiguration(new()
        {
            ["Projection:Document:Providers:Elasticsearch:Enabled"] = "true",
        });

        Action act = () => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Projection:Document:Providers:Elasticsearch is enabled but Endpoints is empty.");
    }

    private static IConfigurationRoot BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
