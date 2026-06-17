import { t } from "@/shared/i18n/messages";
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

export type ResponseErrorPayload = {
  readonly code?: string;
  readonly detail?: string;
  readonly error?: string;
  readonly errors?: unknown;
  readonly message?: string;
  readonly status?: number;
  readonly title?: string;
};

export type ResponseErrorDetails = {
  readonly code?: string;
  readonly message: string;
  readonly status: number;
};

function readResponseErrorFromPayload(
  payload: ResponseErrorPayload,
  response: Pick<Response, "status" | "statusText">
): string {
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

  const validationErrors = readValidationErrors(payload.errors);
  if (validationErrors) {
    return title ? `${title}: ${validationErrors}` : validationErrors;
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

  return "";
}

function readValidationErrors(value: unknown): string | null {
  if (!value || typeof value !== "object") {
    return null;
  }

  const messages: string[] = [];
  for (const [field, fieldErrors] of Object.entries(value)) {
    const normalizedField = normalizeWhitespace(field);
    if (Array.isArray(fieldErrors)) {
      for (const entry of fieldErrors) {
        const message = readJsonErrorText(entry);
        if (!message) {
          continue;
        }

        messages.push(normalizedField ? `${normalizedField}: ${message}` : message);
      }
      continue;
    }

    const message = readJsonErrorText(fieldErrors);
    if (message) {
      messages.push(normalizedField ? `${normalizedField}: ${message}` : message);
    }
  }

  return messages.length > 0 ? messages.join("; ") : null;
}

export async function readResponseErrorDetails(
  response: Pick<Response, "status" | "statusText" | "text">
): Promise<ResponseErrorDetails> {
  const text = await response.text();
  if (!text) {
    return {
      message: formatHttpError(response.status, response.statusText),
      status: response.status,
    };
  }

  try {
    const payload = JSON.parse(text) as ResponseErrorPayload;
    const message =
      readResponseErrorFromPayload(payload, response) || normalizeWhitespace(text);
    return {
      code: readJsonErrorText(payload.code) ?? undefined,
      message,
      status: response.status,
    };
  } catch {
    const htmlSummary = extractHtmlErrorSummary(text);
    if (!htmlSummary) {
      return {
        message: normalizeWhitespace(text),
        status: response.status,
      };
    }

    const httpError = formatHttpError(response.status, response.statusText);
    const normalizedHttpError = httpError.toLowerCase();
    const normalizedHtmlSummary = htmlSummary.toLowerCase();
    const normalizedStatusText = normalizeWhitespace(response.statusText).toLowerCase();

    if (
      normalizedHttpError.includes(normalizedHtmlSummary) ||
      normalizedHtmlSummary.includes(normalizedStatusText)
    ) {
      return {
        message: httpError,
        status: response.status,
      };
    }

    return {
      message: t("shared.api.http.error.copy", "{value1}: {value2}", { value1: httpError, value2: htmlSummary }),
      status: response.status,
    };
  }
}

export async function readResponseError(
  response: Pick<Response, "status" | "statusText" | "text">
): Promise<string> {
  return (await readResponseErrorDetails(response)).message;
}
