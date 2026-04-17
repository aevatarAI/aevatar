using System.Runtime.CompilerServices;

namespace Aevatar.Foundation.VoicePresence.MiniCPM.Internal;

internal static class MiniCPMSsePayloadReader
{
    public static async IAsyncEnumerable<string> ReadPayloadsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (line == null)
            {
                if (dataLines.Count > 0)
                    yield return string.Join('\n', dataLines);

                yield break;
            }

            if (line.Length == 0)
            {
                if (dataLines.Count == 0)
                    continue;

                yield return string.Join('\n', dataLines);
                dataLines.Clear();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var payload = line.Length > 5 ? line[5..] : string.Empty;
            if (payload.StartsWith(' '))
                payload = payload[1..];
            dataLines.Add(payload);
        }
    }
}
