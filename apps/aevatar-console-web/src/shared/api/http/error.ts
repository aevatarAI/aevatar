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

function readJsonErrorText(value: unknown): string | null {
  if (typeof value !== "string") {
    return null;
  }

  const normalized = normalizeWhitespace(value);
  return normalized || null;
}

export async function readResponseError(response: Pick<Response, "status" | "statusText" | "text">): Promise<string> {
  const text = await response.text();
  if (!text) {
    return formatHttpError(response.status, response.statusText);
  }

  try {
    const payload = JSON.parse(text) as {
      code?: string;
      detail?: string;
      error?: string;
      message?: string;
      status?: number;
      title?: string;
    };
    const message = readJsonErrorText(payload.message);
    if (message) {
      return message;
    }

    const error = readJsonErrorText(payload.error);
    if (error) {
      return error;
    }

    const detail = readJsonErrorText(payload.detail);
    const title = readJsonErrorText(payload.title);
    if (detail && title) {
      return `${title}: ${detail}`;
    }

    if (detail) {
      return detail;
    }

    if (title) {
      return title;
    }

    const code = readJsonErrorText(payload.code);
    if (code) {
      return code;
    }

    if (typeof payload.status === "number" && Number.isFinite(payload.status)) {
      return formatHttpError(payload.status, response.statusText);
    }

    return normalizeWhitespace(text);
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
