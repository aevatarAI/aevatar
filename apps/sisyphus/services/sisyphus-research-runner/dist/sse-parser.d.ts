/**
 * Reusable SSE stream parser. Reads a ReadableStream of SSE data and yields
 * parsed JSON objects from `data:` lines.
 */
export declare function parseSseStream(stream: ReadableStream<Uint8Array>, signal?: AbortSignal): AsyncGenerator<Record<string, unknown>>;
//# sourceMappingURL=sse-parser.d.ts.map