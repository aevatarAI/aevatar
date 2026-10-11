import type { TTFBMetric } from 'web-vitals';

export type ApiRequestErrorType =
  | 'http_error'
  | 'network_error'
  | 'aborted'
  | 'timeout';

export type ConsoleEventProperties = {
  api_request: {
    /** Request origin and path, without credentials, query or fragment. */
    url: string;
    method: string;
    /** Zero when fetch rejects before a response is available. */
    status: number;
    /** Dispatch to response headers or fetch rejection, in milliseconds. */
    duration_ms: number;
  } & (
    | { success: true; error_type?: never }
    | { success: false; error_type: ApiRequestErrorType }
  );
  page_ttfb: {
    /** Time from document navigation to the first byte, in milliseconds. */
    ttfbMs: number;
    navigationType: TTFBMetric['navigationType'];
    metricId: string;
    /** Sanitized document URL, which may differ from the current SPA route. */
    $current_url: string;
  };
  workflow_started: { workflowId: string; nodeCount: number };
  workflow_finished: {
    workflowId: string;
    status: 'success' | 'failed';
    /** Elapsed execution time in milliseconds. */
    duration: number;
  };
  workflow_timeout: { workflowId: string; timeoutMs: number };
  sse_disconnect: {
    workflowId: string;
    reason: 'unexpected_eof' | 'stream_error';
  };
  sse_reconnect: { workflowId: string; retryCount: number };
};
