using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aevatar.Bootstrap.Hosting;

public static class AevatarHostObservabilityExtensions
{
    internal static readonly string[] CoreMeterNames =
    [
        "Aevatar.Agents",
        "Aevatar.Api",
        "Aevatar.CQRS.Projection",
        "Aevatar.Kafka.Transport",
    ];

    public static WebApplicationBuilder AddAevatarHostObservability(
        this WebApplicationBuilder builder,
        string defaultServiceName,
        params string[] additionalMeterNames)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);
        ArgumentNullException.ThrowIfNull(additionalMeterNames);

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? defaultServiceName;
        var defaultSamplingRatio = builder.Environment.IsDevelopment() ? 1.0 : 0.1;
        var samplingRatio = ResolveSamplingRatio(builder, defaultSamplingRatio);
        var apiLatencyBucketsMs = ResolveHistogramBuckets(
            builder.Configuration["Observability:Metrics:ApiLatencyBucketsMs"],
            [25d, 50d, 100d, 250d, 500d, 1000d, 2500d, 5000d, 10000d, 20000d, 30000d, 45000d, 60000d, 90000d, 120000d]);
        var runtimeLatencyBucketsMs = ResolveHistogramBuckets(
            builder.Configuration["Observability:Metrics:RuntimeLatencyBucketsMs"],
            [1d, 5d, 10d, 25d, 50d, 100d, 250d, 500d, 1000d, 2500d, 5000d, 10000d]);
        var otlpEndpoint = ResolveOtlpEndpoint(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource("Aevatar.Agents")
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(samplingRatio)));

                if (otlpEndpoint is not null)
                    tracing.AddOtlpExporter(options => options.Endpoint = otlpEndpoint);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(CoreMeterNames)
                    .AddMeter(additionalMeterNames)
                    .AddView(
                        instrumentName: "aevatar.api.request_duration_ms",
                        new ExplicitBucketHistogramConfiguration { Boundaries = apiLatencyBucketsMs })
                    .AddView(
                        instrumentName: "aevatar.api.first_response_duration_ms",
                        new ExplicitBucketHistogramConfiguration { Boundaries = apiLatencyBucketsMs })
                    .AddView(
                        instrumentName: "aevatar.runtime.event_handle_duration_ms",
                        new ExplicitBucketHistogramConfiguration { Boundaries = runtimeLatencyBucketsMs });

                if (otlpEndpoint is not null)
                    metrics.AddOtlpExporter(options => options.Endpoint = otlpEndpoint);
            });

        return builder;
    }

    private static double ResolveSamplingRatio(WebApplicationBuilder builder, double defaultValue)
    {
        var observabilitySampleRatio = builder.Configuration["Observability:Tracing:SampleRatio"];
        var otelSamplerArg = builder.Configuration["OTEL_TRACES_SAMPLER_ARG"];

        var configuredValue = string.IsNullOrWhiteSpace(observabilitySampleRatio)
            ? otelSamplerArg
            : observabilitySampleRatio;
        var configuredKey = string.IsNullOrWhiteSpace(observabilitySampleRatio)
            ? "OTEL_TRACES_SAMPLER_ARG"
            : "Observability:Tracing:SampleRatio";

        if (string.IsNullOrWhiteSpace(configuredValue))
            return defaultValue;

        if (!double.TryParse(configuredValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) ||
            !double.IsFinite(ratio) ||
            ratio < 0d ||
            ratio > 1d)
        {
            throw new InvalidOperationException(
                $"Invalid sampling ratio '{configuredValue}' in '{configuredKey}'. " +
                "Expected a finite number between 0 and 1.");
        }

        return ratio;
    }

    private static Uri? ResolveOtlpEndpoint(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return null;

        if (!Uri.TryCreate(configuredValue, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Invalid OTLP endpoint '{configuredValue}' in 'OTEL_EXPORTER_OTLP_ENDPOINT'. Expected an absolute URI.");
        }

        return uri;
    }

    private static double[] ResolveHistogramBuckets(string? configuredValue, double[] defaultBuckets)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return defaultBuckets;

        var values = configuredValue
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value =>
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
                    !double.IsFinite(parsed) ||
                    parsed <= 0d)
                {
                    throw new InvalidOperationException(
                        $"Invalid histogram bucket '{value}' in '{configuredValue}'. Expected positive finite numbers.");
                }

                return parsed;
            })
            .Distinct()
            .OrderBy(value => value)
            .ToArray();

        if (values.Length == 0)
            throw new InvalidOperationException("Histogram bucket configuration must contain at least one value.");

        return values;
    }
}
