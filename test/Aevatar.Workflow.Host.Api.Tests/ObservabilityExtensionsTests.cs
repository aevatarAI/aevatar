using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Aevatar.Bootstrap.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Aevatar.Workflow.Host.Api.Tests;

public class ObservabilityExtensionsTests
{
    private const double DefaultRatio = 0.1d;
    private const string MaterializerDurationInstrumentName = "aevatar.projection.materializer.duration";
    private const string Neo4jWriteDurationInstrumentName = "aevatar.projection.neo4j.write.duration";
    private static readonly Type ObservabilityExtensionsType = typeof(AevatarHostObservabilityExtensions);

    private static readonly FieldInfo DefaultProjectionLatencyBucketsField =
        ObservabilityExtensionsType.GetField(
            "DefaultProjectionLatencyBucketsMs",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ResolveSamplingRatioMethod =
        ObservabilityExtensionsType.GetMethod("ResolveSamplingRatio", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ResolveOtlpEndpointMethod =
        ObservabilityExtensionsType.GetMethod("ResolveOtlpEndpoint", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ResolveHistogramBucketsMethod =
        ObservabilityExtensionsType.GetMethod("ResolveHistogramBuckets", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo AddObservabilityMethod =
        ObservabilityExtensionsType.GetMethod("AddAevatarHostObservability", BindingFlags.Public | BindingFlags.Static)!;

    [Fact]
    public void ResolveSamplingRatio_WhenNotConfigured_ShouldReturnDefault()
    {
        var builder = CreateBuilder();

        var ratio = InvokeResolveSamplingRatio(builder, DefaultRatio);

        ratio.Should().Be(DefaultRatio);
    }

    [Theory]
    [InlineData("Observability:Tracing:SampleRatio", "0.25", 0.25)]
    [InlineData("OTEL_TRACES_SAMPLER_ARG", "1", 1.0)]
    [InlineData("OTEL_TRACES_SAMPLER_ARG", "0", 0.0)]
    public void ResolveSamplingRatio_WhenValidConfigured_ShouldReturnParsedValue(
        string key,
        string value,
        double expected)
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [key] = value,
        });

        var ratio = InvokeResolveSamplingRatio(builder, DefaultRatio);

        ratio.Should().Be(expected);
    }

    [Theory]
    [InlineData("Observability:Tracing:SampleRatio", "NaN")]
    [InlineData("OTEL_TRACES_SAMPLER_ARG", "Infinity")]
    [InlineData("OTEL_TRACES_SAMPLER_ARG", "-Infinity")]
    [InlineData("Observability:Tracing:SampleRatio", "abc")]
    [InlineData("Observability:Tracing:SampleRatio", "1.5")]
    public void ResolveSamplingRatio_WhenConfiguredInvalid_ShouldThrow(
        string key,
        string value)
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [key] = value,
        });

        var act = () => InvokeResolveSamplingRatio(builder, DefaultRatio);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{value}'*'{key}'*finite number between 0 and 1*");
    }

    [Fact]
    public void ResolveOtlpEndpoint_WhenNotConfigured_ShouldReturnNull()
    {
        InvokeResolveOtlpEndpoint(null).Should().BeNull();
        InvokeResolveOtlpEndpoint(" ").Should().BeNull();
    }

    [Fact]
    public void ResolveOtlpEndpoint_WhenValidAbsoluteUri_ShouldReturnUri()
    {
        var endpoint = InvokeResolveOtlpEndpoint("https://otel.example.com:4317");

        endpoint.Should().NotBeNull();
        endpoint!.AbsoluteUri.Should().Be("https://otel.example.com:4317/");
    }

    [Fact]
    public void ResolveOtlpEndpoint_WhenInvalid_ShouldThrow()
    {
        var act = () => InvokeResolveOtlpEndpoint("relative/path");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OTEL_EXPORTER_OTLP_ENDPOINT*absolute URI*");
    }

    [Fact]
    public void ResolveHistogramBuckets_WhenNotConfigured_ShouldReturnDefaults()
    {
        var defaults = new[] { 10d, 20d, 30d };

        var resolved = InvokeResolveHistogramBuckets(null, defaults);

        resolved.Should().Equal(defaults);
    }

    [Fact]
    public void ResolveHistogramBuckets_WhenConfigured_ShouldSortAndDeduplicate()
    {
        var resolved = InvokeResolveHistogramBuckets("50, 10, 10, 25", [1d]);

        resolved.Should().Equal(10d, 25d, 50d);
    }

    [Theory]
    [InlineData("0,10")]
    [InlineData("-1,10")]
    [InlineData("abc")]
    [InlineData(" , ")]
    public void ResolveHistogramBuckets_WhenConfiguredInvalid_ShouldThrow(string configuredValue)
    {
        var act = () => InvokeResolveHistogramBuckets(configuredValue, [1d, 2d]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddAevatarHostObservability_ShouldRegisterTelemetry_WithAndWithoutOtlp()
    {
        var productionBuilder = CreateBuilder(new Dictionary<string, string?>
        {
            ["OTEL_SERVICE_NAME"] = "wf-service",
            ["Observability:Tracing:SampleRatio"] = "0.4",
            ["Observability:Metrics:ApiLatencyBucketsMs"] = "5,10,20",
            ["Observability:Metrics:RuntimeLatencyBucketsMs"] = "1,2,3",
            ["Observability:Metrics:ProjectionLatencyBucketsMs"] = "100,1000,10000,60000",
        });
        var developmentBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        developmentBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://otel.example.com:4317",
        });

        var returnedProduction = InvokeAddObservability(productionBuilder, "wf-default");
        var returnedDevelopment = InvokeAddObservability(developmentBuilder, "wf-default");

        returnedProduction.Should().BeSameAs(productionBuilder);
        returnedDevelopment.Should().BeSameAs(developmentBuilder);
        productionBuilder.Services.Should().NotBeEmpty();
        developmentBuilder.Services.Should().NotBeEmpty();
    }

    [Fact]
    public void AddAevatarHostObservability_ShouldApplyDefaultProjectionBucketsToBothDurationViews()
    {
        var defaultBuckets = GetDefaultProjectionLatencyBuckets();
        defaultBuckets.Should().BeInAscendingOrder();
        defaultBuckets.Last().Should().Be(600000d);
        var exporter = new HistogramBoundsExporter(
            MaterializerDurationInstrumentName,
            Neo4jWriteDurationInstrumentName);
        var builder = CreateBuilder();
        InvokeAddObservability(builder, "wf-default");
        builder.Services
            .AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddReader(new BaseExportingMetricReader(exporter)));

        using var app = builder.Build();
        var provider = app.Services.GetRequiredService<MeterProvider>();
        var testVersion = $"view-test-{Guid.NewGuid():N}";
        using var materializerMeter = new Meter("Aevatar.CQRS.Projection", testVersion);
        using var neo4jMeter = new Meter("Aevatar.CQRS.Projection.Providers.Neo4j", testVersion);
        var materializerDuration = materializerMeter.CreateHistogram<double>(
            MaterializerDurationInstrumentName,
            unit: "ms");
        var neo4jWriteDuration = neo4jMeter.CreateHistogram<double>(
            Neo4jWriteDurationInstrumentName,
            unit: "ms");

        materializerDuration.Record(1d);
        neo4jWriteDuration.Record(1d);

        provider.ForceFlush(10000).Should().BeTrue();
        exporter.GetBounds(MaterializerDurationInstrumentName).Should().Equal(defaultBuckets);
        exporter.GetBounds(Neo4jWriteDurationInstrumentName).Should().Equal(defaultBuckets);
    }

    private static WebApplicationBuilder CreateBuilder(Dictionary<string, string?>? values = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });

        if (values is not null)
            builder.Configuration.AddInMemoryCollection(values);

        return builder;
    }

    private static double[] GetDefaultProjectionLatencyBuckets() =>
        ((double[])DefaultProjectionLatencyBucketsField.GetValue(null)!).ToArray();

    private static double InvokeResolveSamplingRatio(WebApplicationBuilder builder, double defaultValue)
    {
        try
        {
            return (double)ResolveSamplingRatioMethod.Invoke(null, [builder, defaultValue])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static Uri? InvokeResolveOtlpEndpoint(string? value)
    {
        try
        {
            return (Uri?)ResolveOtlpEndpointMethod.Invoke(null, [value]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static double[] InvokeResolveHistogramBuckets(string? configuredValue, double[] defaults)
    {
        try
        {
            return (double[])ResolveHistogramBucketsMethod.Invoke(null, [configuredValue, defaults])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static WebApplicationBuilder InvokeAddObservability(WebApplicationBuilder builder, string defaultServiceName)
    {
        try
        {
            return (WebApplicationBuilder)AddObservabilityMethod.Invoke(
                null,
                [builder, defaultServiceName, new[] { "Aevatar.Workflow" }])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private sealed class HistogramBoundsExporter : BaseExporter<Metric>
    {
        private readonly HashSet<string> _expectedInstrumentNames;
        private readonly Dictionary<string, double[]> _boundsByInstrument = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public HistogramBoundsExporter(params string[] expectedInstrumentNames)
        {
            _expectedInstrumentNames = expectedInstrumentNames.ToHashSet(StringComparer.Ordinal);
        }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (!_expectedInstrumentNames.Contains(metric.Name))
                    continue;

                foreach (var metricPoint in metric.GetMetricPoints())
                {
                    var bounds = new List<double>();
                    foreach (var bucket in metricPoint.GetHistogramBuckets())
                    {
                        if (!double.IsPositiveInfinity(bucket.ExplicitBound))
                            bounds.Add(bucket.ExplicitBound);
                    }

                    lock (_gate)
                    {
                        _boundsByInstrument[metric.Name] = bounds.ToArray();
                    }

                    break;
                }
            }

            return ExportResult.Success;
        }

        public double[] GetBounds(string instrumentName)
        {
            lock (_gate)
            {
                return _boundsByInstrument[instrumentName].ToArray();
            }
        }
    }
}
