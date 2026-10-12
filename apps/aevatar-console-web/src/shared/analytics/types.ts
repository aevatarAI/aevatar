import type { TTFBMetric } from 'web-vitals';

export type ApiRequestErrorType =
  | 'http_error'
  | 'network_error'
  | 'aborted'
  | 'timeout';

export type WorkflowStartSource = 'run_started' | 'activity' | 'first_step';

export type ConsoleEventProperties = {
  long_task: {
    duration_ms: number;
    /** Portion beyond 50 ms for this task; not the Lighthouse TBT metric. */
    blocking_time_ms: number;
    /** Task start relative to this document's performance time origin. */
    start_time_ms: number;
    /** Document URL before observation starts; current SPA URL for later tasks. */
    $current_url: string;
    /** Path from the same sanitized URL, including for buffered startup tasks. */
    $pathname: string;
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
  workflow_started: {
    scopeId: string;
    workflowId: string;
    /** Client analytics correlation only; never a backend run or command ID. */
    invocationId: string;
    nodeCount: number;
    start_source: WorkflowStartSource;
  };
  workflow_finished: {
    scopeId: string;
    workflowId: string;
    /** Reuses the same observer's workflow_started invocationId. */
    invocationId: string;
    status: 'success' | 'failed';
    /** Elapsed execution time in milliseconds. */
    duration: number;
    /** first_step excludes the wait before the first observed executing step. */
    start_source: WorkflowStartSource;
  };
  workflow_timeout: { workflowId: string; timeoutMs: number };
  sse_disconnect: {
    workflowId: string;
    reason: 'unexpected_eof' | 'stream_error';
  };
  sse_reconnect: { workflowId: string; retryCount: number };
};
