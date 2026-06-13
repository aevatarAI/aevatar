using Aevatar.Foundation.Runtime.Callbacks;
using Google.Protobuf;
using Orleans.Storage;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

internal sealed class RuntimeCallbackSchedulerStateGrainStorageSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T input)
    {
        if (input is not RuntimeCallbackSchedulerState state)
            throw new InvalidOperationException(
                $"{nameof(RuntimeCallbackSchedulerStateGrainStorageSerializer)} only supports {nameof(RuntimeCallbackSchedulerState)}.");

        return BinaryData.FromBytes(state.ToByteArray());
    }

    public T Deserialize<T>(BinaryData input)
    {
        if (typeof(T) != typeof(RuntimeCallbackSchedulerState))
            throw new InvalidOperationException(
                $"{nameof(RuntimeCallbackSchedulerStateGrainStorageSerializer)} only supports {nameof(RuntimeCallbackSchedulerState)}.");

        var state = RuntimeCallbackSchedulerState.Parser.ParseFrom(input.ToArray());
        return (T)(object)state;
    }
}
