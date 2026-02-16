using System.Threading.Channels;

namespace Aevatar.Presentation.AGUI;

/// <summary>
/// Runtime options for AG-UI event sink buffering behavior.
/// </summary>
public sealed class AGUIEventChannelOptions
{
    /// <summary>Per-request queue capacity.</summary>
    public int Capacity { get; set; } = 1024;

    /// <summary>Behavior when queue is full.</summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;
}
