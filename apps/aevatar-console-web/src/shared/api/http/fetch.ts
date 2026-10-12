import { captureConsoleEvent } from '@/shared/analytics/posthog';
import type { ApiRequestErrorType } from '@/shared/analytics/types';

type RequestMeasurement = {
  url: string;
  method: string;
  startTime: number;
};

function startRequestMeasurement(
  input: RequestInfo | URL,
  init?: RequestInit,
): RequestMeasurement | undefined {
  try {
    const startTime = performance.now();
    const request =
      typeof Request !== 'undefined' && input instanceof Request
        ? input
        : undefined;
    const rawUrl = request?.url ?? String(input);
    let url = '[redacted]';
    try {
      const parsed = new URL(
        rawUrl,
        typeof window === 'undefined' ? undefined : window.location.href,
      );
      if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
        url = `${parsed.origin}${parsed.pathname}`;
      }
    } catch {
      // Invalid URLs still follow the native fetch rejection path.
    }
    return {
      startTime,
      url,
      method: (init?.method ?? request?.method ?? 'GET').toUpperCase(),
    };
  } catch {
    // Performance or request metadata must never prevent a request.
    return undefined;
  }
}

function fetchErrorType(error: unknown): ApiRequestErrorType {
  if (error && typeof error === 'object' && 'name' in error) {
    if (error.name === 'AbortError') return 'aborted';
    if (error.name === 'TimeoutError') return 'timeout';
  }
  return 'network_error';
}

function finishRequestMeasurement(
  measurement: RequestMeasurement | undefined,
  result: { response: Response } | { error: unknown },
): void {
  if (!measurement) return;
  try {
    const duration = performance.now() - measurement.startTime;
    if (!Number.isFinite(duration)) return;
    const response = 'response' in result ? result.response : undefined;
    const properties = {
      url: measurement.url,
      method: measurement.method,
      status: response?.status ?? 0,
      duration_ms: Math.max(0, duration),
    };
    captureConsoleEvent(
      'api_request',
      response?.ok
        ? { ...properties, success: true }
        : {
            ...properties,
            success: false,
            error_type:
              'error' in result ? fetchErrorType(result.error) : 'http_error',
          },
    );
  } catch {
    // Telemetry cannot replace either a response or the original fetch error.
  }
}

/** Measures dispatch to response headers, preserving bodies and SSE streams. */
export async function apiFetch(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<Response> {
  const measurement = startRequestMeasurement(input, init);
  let response: Response;
  try {
    response =
      init === undefined ? await fetch(input) : await fetch(input, init);
  } catch (error) {
    finishRequestMeasurement(measurement, { error });
    throw error;
  }
  finishRequestMeasurement(measurement, { response });
  return response;
}
