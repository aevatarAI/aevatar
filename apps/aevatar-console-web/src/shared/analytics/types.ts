import type { TTFBMetric } from 'web-vitals';

export type ApiRequestErrorType =
  | 'http_error'
  | 'network_error'
  | 'aborted'
  | 'timeout';

export type ConsoleEventProperties = {
  long_task: {
    duration_ms: number;
    /** Portion beyond 50 ms for this task; not the Lighthouse TBT metric. */
    blocking_time_ms: number;
    /** Task start relative to this document's performance time origin. */
    start_time_ms: number;
    /** Sanitized current page URL when the observer delivers the task. */
    $current_url: string;
  };
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
