using System.Runtime.InteropServices;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

internal sealed class KafkaReceiverMessageBuffer
{
    private readonly IBatchContainer?[] _messages;
    private PaddedIndex _readIndex;
    private PaddedIndex _writeIndex;

    public KafkaReceiverMessageBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _messages = new IBatchContainer[capacity + 1];
    }

    public int Capacity { get; }

    public int Depth
    {
        get
        {
            var readIndex = Volatile.Read(ref _readIndex.Value);
            var writeIndex = Volatile.Read(ref _writeIndex.Value);
            return writeIndex >= readIndex
                ? writeIndex - readIndex
                : _messages.Length - readIndex + writeIndex;
        }
    }

    public bool TryWrite(IBatchContainer message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Kafka's owner loop is the sole writer; Orleans assigns one puller to this queue receiver.
        var writeIndex = _writeIndex.Value;
        var nextWriteIndex = NextIndex(writeIndex);
        if (nextWriteIndex == Volatile.Read(ref _readIndex.Value))
            return false;

        _messages[writeIndex] = message;
        Volatile.Write(ref _writeIndex.Value, nextWriteIndex);
        return true;
    }

    public bool TryRead(out IBatchContainer? message)
    {
        var readIndex = _readIndex.Value;
        if (readIndex == Volatile.Read(ref _writeIndex.Value))
        {
            message = null;
            return false;
        }

        message = _messages[readIndex];
        _messages[readIndex] = null;
        Volatile.Write(ref _readIndex.Value, NextIndex(readIndex));
        return true;
    }

    public void Clear()
    {
        while (TryRead(out _))
        {
        }
    }

    private int NextIndex(int index) => ++index == _messages.Length ? 0 : index;

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedIndex
    {
        [FieldOffset(64)]
        internal int Value;
    }
}
