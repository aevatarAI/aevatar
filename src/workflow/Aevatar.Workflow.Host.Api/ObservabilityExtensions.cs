using Microsoft.AspNetCore.Builder;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Globalization;

namespace Aevatar.Workflow.Host.Api;

internal static class ObservabilityExtensions
{
    /// <summary>
    /// Registers OpenTelemetry tracing. Service name is resolved from
    /// OTEL_SERVICE_NAME; OTLP exporter is enabled when
    /// OTEL_EXPORTER_OTLP_ENDPOINT is set.
    /// </summary>
    internal static WebApplicationBuilder AddAevatarWorkflowObservability(
        this WebApplicationBuilder builder,
        string defaultServiceName = "Aevatar.Workflow.Host.Api")
    {
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? defaultServiceName;
        var defaultSamplingRatio = builder.Environment.IsDevelopment() ? 1.0 : 0.1;
        var samplingRatio = ResolveSamplingRatio(builder, defaultSamplingRatio);

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

                var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
                if (!string.IsNullOrWhiteSpace(endpoint) &&
                    Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = uri);
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Aevatar.Agents")
                    .AddMeter("Aevatar.Api")
                    .AddPrometheusExporter();
            });

        return builder;
    }

    private static double ResolveSamplingRatio(WebApplicationBuilder builder, double defaultValue)
    {
        var configuredValue =
            builder.Configuration["Observability:Tracing:SampleRatio"] ??
            builder.Configuration["OTEL_TRACES_SAMPLER_ARG"];

        if (string.IsNullOrWhiteSpace(configuredValue))
            return defaultValue;

        if (!double.TryParse(configuredValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            return defaultValue;

        return Math.Clamp(ratio, 0d, 1d);
    }
}
