using Microsoft.AspNetCore.Builder;
using OpenTelemetry;
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

        if (!double.TryParse(configuredValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            throw new InvalidOperationException(
                $"Invalid sampling ratio '{configuredValue}' in '{configuredKey}'. " +
                "Expected a finite number between 0 and 1.");

        if (!double.IsFinite(ratio) || ratio < 0d || ratio > 1d)
            throw new InvalidOperationException(
                $"Invalid sampling ratio '{configuredValue}' in '{configuredKey}'. " +
                "Expected a finite number between 0 and 1.");

        return ratio;
    }
}
