using System.Diagnostics.Metrics;

namespace Aevatar.Workflow.Host.Api.Tests.Helpers;

internal sealed class ApiMetricCapture : IDisposable
{
    private const string ApiMeterName = "Aevatar.Api";
    private const string FirstResponseMetricName = "aevatar.api.first_response_duration_ms";
    private const string RequestsTotalMetricName = "aevatar.api.requests_total";
    private readonly MeterListener _listener = new();

    public ApiMetricCapture()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ApiMeterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == FirstResponseMetricName)
                FirstResponseMeasurements.Add(new MetricMeasurement(measurement, tags.ToArray()));
        });

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == RequestsTotalMetricName)
                RequestMeasurements.Add(new MetricMeasurement(measurement, tags.ToArray()));
        });

        _listener.Start();
    }

    public List<MetricMeasurement> FirstResponseMeasurements { get; } = [];
    public List<MetricMeasurement> RequestMeasurements { get; } = [];

    public void Dispose()
    {
        _listener.Dispose();
    }
}

internal sealed record MetricMeasurement(double Value, KeyValuePair<string, object?>[] Tags);
