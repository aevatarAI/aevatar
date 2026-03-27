function normalizeWhitespace(value: string | null | undefined): string {
  return String(value ?? "")
    .replace(/\s+/g, " ")
    .trim();
}

function formatHttpError(status: number, statusText: string): string {
  const normalizedStatusText = normalizeWhitespace(statusText);
  return normalizedStatusText ? `HTTP ${status} ${normalizedStatusText}` : `HTTP ${status}`;
}

function stripHtmlTags(value: string): string {
  return value
    .replace(/<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>/gi, " ")
    .replace(/<style\b[^<]*(?:(?!<\/style>)<[^<]*)*<\/style>/gi, " ")
    .replace(/<[^>]+>/g, " ");
}

function extractHtmlErrorSummary(value: string): string | null {
  const trimmed = value.trimStart();
  const looksLikeHtml =
    /^<!doctype html/i.test(trimmed) ||
    /^<html[\s>]/i.test(trimmed) ||
    /<body[\s>]/i.test(trimmed) ||
    /<title[\s>]/i.test(trimmed);

  if (!looksLikeHtml) {
    return null;
  }

  const titleMatch = value.match(/<title[^>]*>([\s\S]*?)<\/title>/i);
  const headingMatch = value.match(/<h1[^>]*>([\s\S]*?)<\/h1>/i);
  const summary = normalizeWhitespace(
    stripHtmlTags(titleMatch?.[1] ?? headingMatch?.[1] ?? value)
  );

  return summary || null;
}

export async function readResponseError(response: Pick<Response, "status" | "statusText" | "text">): Promise<string> {
  const text = await response.text();
  if (!text) {
    return formatHttpError(response.status, response.statusText);
  }

  try {
    const payload = JSON.parse(text) as {
      code?: string;
      error?: string;
      message?: string;
    };
    return payload.message || payload.error || payload.code || text;
  } catch {
    const htmlSummary = extractHtmlErrorSummary(text);
    if (!htmlSummary) {
      return normalizeWhitespace(text);
    }

    const httpError = formatHttpError(response.status, response.statusText);
    const normalizedHttpError = httpError.toLowerCase();
    const normalizedHtmlSummary = htmlSummary.toLowerCase();
    const normalizedStatusText = normalizeWhitespace(response.statusText).toLowerCase();

    if (
      normalizedHttpError.includes(normalizedHtmlSummary) ||
      normalizedHtmlSummary.includes(normalizedStatusText)
    ) {
      return httpError;
    }

    return `${httpError}: ${htmlSummary}`;
  }
}
