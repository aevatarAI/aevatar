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

    private static IConfigurationRoot BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
