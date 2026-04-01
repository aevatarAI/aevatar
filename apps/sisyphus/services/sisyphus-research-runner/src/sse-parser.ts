/**
 * Reusable SSE stream parser. Reads a ReadableStream of SSE data and yields
 * parsed JSON objects from `data:` lines.
 */
export async function* parseSseStream(
  stream: ReadableStream<Uint8Array>,
  signal?: AbortSignal
): AsyncGenerator<Record<string, unknown>> {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  try {
    while (!signal?.aborted) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";

      for (const line of lines) {
        if (!line.startsWith("data: ")) continue;
        const data = line.slice(6).trim();
        if (!data || data === "[DONE]") continue;

        try {
          yield JSON.parse(data) as Record<string, unknown>;
        } catch {
          // Non-JSON data line, skip
        }
      }
    }
  } finally {
    reader.releaseLock();
  }
}
