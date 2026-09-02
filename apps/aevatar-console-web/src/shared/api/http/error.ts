import { t } from '@/shared/i18n/messages';

function normalizeWhitespace(value: string | null | undefined): string {
  return String(value ?? '')
    .replace(/\s+/g, ' ')
    .trim();
}

function formatHttpError(status: number, statusText: string): string {
  const normalizedStatusText = normalizeWhitespace(statusText);
  return normalizedStatusText
    ? `HTTP ${status} ${normalizedStatusText}`
    : `HTTP ${status}`;
}

function stripHtmlTags(value: string): string {
  return value
    .replace(/<script\b[^<]*(?:(?!<\/script>)<[^<]*)*<\/script>/gi, ' ')
    .replace(/<style\b[^<]*(?:(?!<\/style>)<[^<]*)*<\/style>/gi, ' ')
    .replace(/<[^>]+>/g, ' ');
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
    stripHtmlTags(titleMatch?.[1] ?? headingMatch?.[1] ?? value),
  );

  return summary || null;
}

function readJsonErrorText(value: unknown): string | null {
  if (typeof value !== 'string') {
    return null;
  }

  const normalized = normalizeWhitespace(value);
  return normalized || null;
}

export type ResponseErrorPayload = {
  readonly code?: string;
  readonly correlationId?: string;
  readonly detail?: string;
  readonly error?: string;
  readonly errors?: unknown;
  readonly message?: string;
  readonly status?: number;
  readonly title?: string;
  readonly retryAfterSeconds?: number;
};

export type ResponseErrorDetails = {
  readonly code?: string;
  readonly correlationId?: string;
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>>;
  readonly message: string;
  readonly preflightLocator?: string;
  readonly requiredStateVersion?: number;
  readonly retryable?: boolean;
  readonly retryAfterSeconds?: number;
  readonly status: number;
};

type ErrorResponse = Pick<Response, 'status' | 'statusText' | 'text'> & {
  readonly headers?: Pick<Headers, 'get'>;
};

function readRetryAfterSeconds(
  payload: ResponseErrorPayload,
  response: ErrorResponse,
): number | undefined {
  if (
    typeof payload.retryAfterSeconds === 'number' &&
    Number.isFinite(payload.retryAfterSeconds) &&
    payload.retryAfterSeconds >= 0
  ) {
    return payload.retryAfterSeconds;
  }

  const headerValue = response.headers?.get('Retry-After')?.trim() ?? '';
  if (!/^\d+(?:\.\d+)?$/.test(headerValue)) return undefined;
  const seconds = Number(headerValue);
  return Number.isFinite(seconds) ? Math.ceil(seconds) : undefined;
}

function readResponseErrorFromPayload(
  payload: ResponseErrorPayload,
  response: Pick<Response, 'status' | 'statusText'>,
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

  if (typeof payload.status === 'number' && Number.isFinite(payload.status)) {
    return formatHttpError(payload.status, response.statusText);
  }

  return '';
}

function readValidationErrorMap(
  value: unknown,
): Readonly<Record<string, readonly string[]>> | undefined {
  if (!value || typeof value !== 'object') {
    return undefined;
  }

  const fields: Record<string, string[]> = {};
  for (const [field, fieldErrors] of Object.entries(value)) {
    const normalizedField = normalizeWhitespace(field);
    if (!normalizedField) continue;
    const messages: string[] = [];
    if (Array.isArray(fieldErrors)) {
      for (const entry of fieldErrors) {
        const message = readJsonErrorText(entry);
        if (message) messages.push(message);
      }
    } else {
      const message = readJsonErrorText(fieldErrors);
      if (message) messages.push(message);
    }
    if (messages.length > 0) fields[normalizedField] = messages;
  }

  return Object.keys(fields).length > 0 ? fields : undefined;
}

function readValidationErrors(value: unknown): string | null {
  const fields = readValidationErrorMap(value);
  if (!fields) return null;
  return Object.entries(fields)
    .flatMap(([field, messages]) =>
      messages.map((message) => `${field}: ${message}`),
    )
    .join('; ');
}

export async function readResponseErrorDetails(
  response: ErrorResponse,
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
      readResponseErrorFromPayload(payload, response) ||
      normalizeWhitespace(text);
    return {
      code:
        readJsonErrorText(payload.code) ??
        readJsonErrorText(payload.error) ??
        undefined,
      correlationId: readJsonErrorText(payload.correlationId) ?? undefined,
      fieldErrors: readValidationErrorMap(payload.errors),
      message,
      preflightLocator:
        readJsonErrorText(
          (payload as ResponseErrorPayload & { preflightLocator?: unknown })
            .preflightLocator,
        ) ?? undefined,
      requiredStateVersion:
        typeof (
          payload as ResponseErrorPayload & { requiredStateVersion?: unknown }
        ).requiredStateVersion === 'number'
          ? (payload as ResponseErrorPayload & { requiredStateVersion: number })
              .requiredStateVersion
          : undefined,
      retryable:
        typeof (payload as ResponseErrorPayload & { retryable?: unknown })
          .retryable === 'boolean'
          ? (payload as ResponseErrorPayload & { retryable: boolean }).retryable
          : undefined,
      retryAfterSeconds: readRetryAfterSeconds(payload, response),
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
    const normalizedStatusText = normalizeWhitespace(
      response.statusText,
    ).toLowerCase();

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
      message: t('shared.api.http.error.copy', '{value1}: {value2}', {
        value1: httpError,
        value2: htmlSummary,
      }),
      status: response.status,
    };
  }
}

export async function readResponseError(
  response: ErrorResponse,
): Promise<string> {
  return (await readResponseErrorDetails(response)).message;
}
