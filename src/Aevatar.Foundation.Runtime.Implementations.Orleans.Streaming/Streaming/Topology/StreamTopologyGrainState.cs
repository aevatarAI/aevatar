using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Topology;

[GenerateSerializer]
public sealed class StreamTopologyGrainState
{
    [Id(0)]
    public List<StreamForwardingBindingEntry> Bindings { get; set; } = [];

    [Id(1)]
    public Dictionary<string, StreamForwardingBindingEntry> BindingsByTarget { get; set; } = [];

    [Id(2)]
    public long Revision { get; set; }
}

[GenerateSerializer]
public sealed class StreamForwardingBindingEntry
{
    [Id(0)]
    public string SourceStreamId { get; set; } = string.Empty;

    [Id(1)]
    public string TargetStreamId { get; set; } = string.Empty;

    [Id(2)]
    public StreamForwardingMode ForwardingMode { get; set; } = StreamForwardingMode.HandleThenForward;

    [Id(3)]
    public List<EventDirection> DirectionFilter { get; set; } = [];

    [Id(4)]
    public List<string> EventTypeFilter { get; set; } = [];

    [Id(5)]
    public long Version { get; set; }

    [Id(6)]
    public string? LeaseId { get; set; }
}
