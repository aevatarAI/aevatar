/**
 * Shared SSE stream parser. Reads a ReadableStream of SSE `data:` lines,
 * parses each JSON payload, and dispatches to the onEvent callback.
 *
 * Handles buffering across chunk boundaries and flushes any trailing
 * partial `data:` line when the stream ends.
 */
export async function parseSseStream<T = unknown>(
  reader: ReadableStreamDefaultReader<Uint8Array>,
  onEvent: (event: T) => void,
): Promise<void> {
  const decoder = new TextDecoder()
  let buffer = ''

  while (true) {
    const { done, value } = await reader.read()
    if (done) break

    buffer += decoder.decode(value, { stream: true })
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''

    for (const line of lines) {
      const parsed = parseSseLine<T>(line)
      if (parsed !== null) onEvent(parsed)
    }
  }

  // Flush remaining buffer
  const parsed = parseSseLine<T>(buffer)
  if (parsed !== null) onEvent(parsed)
}

/**
 * Parse a single SSE line. Returns the parsed JSON object if the line
 * is a valid `data: {...}` line, or null otherwise.
 */
function parseSseLine<T>(line: string): T | null {
  const trimmed = line.trim()
  if (!trimmed.startsWith('data: ')) return null
  const jsonStr = trimmed.slice(6)
  if (!jsonStr) return null
  try {
    return JSON.parse(jsonStr) as T
  } catch {
    return null
  }
}

/**
 * Convenience: given a fetch Response, read its body as an SSE stream.
 * Validates the response has a readable body before proceeding.
 */
export async function readSseResponse<T = unknown>(
  res: Response,
  onEvent: (event: T) => void,
): Promise<void> {
  const reader = res.body?.getReader()
  if (!reader) throw new Error('No response body')
  return parseSseStream(reader, onEvent)
}
