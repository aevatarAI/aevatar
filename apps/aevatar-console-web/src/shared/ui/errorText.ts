function normalizeWhitespace(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}

export function describeError(
  error: unknown,
  fallback = "Unexpected error."
): string {
  if (error instanceof Error) {
    const message = normalizeWhitespace(error.message || error.name || "");
    return message || fallback;
  }

  if (error && typeof error === "object" && !Array.isArray(error)) {
    const record = error as { message?: unknown };
    if (typeof record.message === "string") {
      const message = normalizeWhitespace(record.message);
      if (message) {
        return message;
      }
    }
  }

  const text = normalizeWhitespace(String(error ?? ""));
  return text || fallback;
}
