using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aevatar.Foundation.Runtime.Observability;

namespace Aevatar.Demos.Inspector.Telemetry;

public sealed class InspectorTelemetryBroadcaster : IDisposable
{
    private readonly Channel<TelemetryFrame> _frames;
    private readonly ActivityListener _listener;

    public InspectorTelemetryBroadcaster()
        : this(capacity: 1000)
    {
    }

    public InspectorTelemetryBroadcaster(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Telemetry channel capacity must be positive.");

        _frames = Channel.CreateBounded<TelemetryFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });

        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == AevatarActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => TryPublish(FromActivity(activity)),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public async IAsyncEnumerable<TelemetryFrame> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _frames.Reader.WaitToReadAsync(ct))
        {
            while (_frames.Reader.TryRead(out var frame))
                yield return frame;
        }
    }

    public bool TryPublish(TelemetryFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _frames.Writer.TryWrite(frame);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    private static TelemetryFrame FromActivity(Activity activity)
    {
        var tags = activity
            .TagObjects
            .Where(tag => tag.Value != null)
            .ToDictionary(
                tag => tag.Key,
                tag => tag.Value?.ToString() ?? string.Empty,
                StringComparer.Ordinal);

        return new TelemetryFrame(
            activity.Id ?? Guid.NewGuid().ToString("N"),
            activity.TraceId.ToString(),
            activity.SpanId.ToString(),
            activity.DisplayName,
            activity.StartTimeUtc == default
                ? DateTimeOffset.UtcNow
                : new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
            activity.Duration.TotalMilliseconds,
            activity.Status.ToString(),
            new ReadOnlyDictionary<string, string>(tags));
    }
}
